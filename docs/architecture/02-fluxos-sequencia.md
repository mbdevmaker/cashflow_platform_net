# Fluxos e Diagramas de Sequência

## 1. Fluxo de escrita — registrar lançamento

```mermaid
sequenceDiagram
    autonumber
    actor C as Cliente
    participant GW as Gateway/Ingress
    participant API as CashFlow.Api
    participant APP as RegisterTransactionHandler
    participant DB as postgres-cashflow

    C->>GW: POST /api/v1/transactions<br/>Authorization: Bearer · Idempotency-Key
    GW->>GW: TLS, rate limit, tamanho de payload
    GW->>API: encaminha
    API->>API: valida JWT (issuer, audience, assinatura, exp)<br/>resolve MerchantId das claims → ITenantContext
    API->>APP: RegisterTransactionCommand
    APP->>DB: BEGIN
    APP->>DB: SELECT IdempotencyRecords (MerchantId, 'register-transaction', Key)
    alt chave já existe com mesmo RequestHash
        APP-->>API: resultado original (snapshot)
        API-->>C: 201 Created (mesmo corpo) + header Idempotent-Replayed: true
    else chave existe com hash diferente
        APP-->>API: IdempotencyConflict
        API-->>C: 409 Conflict (ProblemDetails)
    else nova
        APP->>DB: SELECT CashAccount WHERE Id AND MerchantId (FOR SHARE)
        APP->>APP: Domain: valida valor>0, moeda da conta,<br/>BusinessDate = OccurredAt no fuso IANA da conta
        APP->>DB: UPDATE CashAccounts SET LastSequence = LastSequence+1 RETURNING (accountSequence)
        APP->>DB: INSERT CashTransactions
        APP->>DB: INSERT IdempotencyRecords (hash + snapshot)
        APP->>DB: INSERT OutboxMessages (CashTransactionCreated v1 + traceparent)
        APP->>DB: COMMIT
        API-->>C: 201 Created + Location
    end
```

**Pontos de defesa:**
- RabbitMQ **não está** no caminho da resposta. O `201` depende só do PostgreSQL do Cash Flow.
- `INSERT` de transação e de Outbox são **atômicos**: ou ambos existem, ou nenhum.
- Concorrência de duas requisições com a mesma chave: a constraint `UNIQUE (MerchantId, Operation, IdempotencyKey)` faz a segunda falhar no commit; o handler captura a violação e devolve o resultado da primeira (ou `409`).
- A ordem de UPDATE em `CashAccounts` serializa escritas da mesma conta (row lock curto) e gera `accountSequence` sem lacunas, usada no watermark da consolidação.

## 2. Fluxo de publicação — Outbox Publisher

```mermaid
sequenceDiagram
    autonumber
    participant W as OutboxPublisher (N réplicas)
    participant DB as postgres-cashflow
    participant MQ as RabbitMQ

    loop a cada 250–500 ms ou NOTIFY
        W->>DB: BEGIN
        W->>DB: SELECT ... FROM OutboxMessages<br/>WHERE PublishedAt IS NULL AND NextAttemptAt <= now()<br/>ORDER BY CreatedAt LIMIT 100<br/>FOR UPDATE SKIP LOCKED
        loop para cada mensagem
            W->>MQ: basic.publish (persistent, MessageId=EventId, traceparent)
            MQ-->>W: basic.ack (publisher confirm — após replicação no quorum)
            W->>DB: UPDATE PublishedAt = now()
        end
        alt broker indisponível / nack / timeout
            W->>DB: UPDATE RetryCount+1, LastError, NextAttemptAt = now()+backoff
        end
        W->>DB: COMMIT
    end
```

**Janela de duplicidade aceita:** se o worker cair entre o `ack` do broker e o `COMMIT`, a linha volta a ficar disponível e é republicada com o **mesmo `EventId`**. O consumidor deduplica. Isso é preferível à alternativa (marcar antes de publicar), que perderia eventos.

**Transação curta:** o lote é limitado (ex.: 100) e o timeout de publish é curto (ex.: 5 s) para não segurar locks. Alternativa equivalente: claim com `UPDATE ... SET LockedUntil = now()+30s ... RETURNING` fora de transação longa, e publicação sem transação aberta — recomendada se lotes maiores forem necessários (SPEC: "não manter transação aberta durante chamadas de rede").

## 3. Fluxo de consumo — consolidação idempotente

```mermaid
sequenceDiagram
    autonumber
    participant MQ as RabbitMQ
    participant MT as MassTransit (retry/redelivery)
    participant H as ApplyCashMovementHandler
    participant DB as postgres-consolidation
    participant R as Redis

    MQ->>MT: deliver (prefetch N)
    MT->>H: CashTransactionCreated v1
    H->>H: valida schemaVersion, campos obrigatórios, amount>0, currency
    H->>DB: BEGIN
    H->>DB: SELECT pg_advisory_xact_lock(hashtextextended(merchantId||accountId, 0))
    H->>DB: INSERT ProcessedEvents(EventId, ConsumerName) ON CONFLICT DO NOTHING
    alt 0 linhas inseridas (duplicado)
        H->>DB: COMMIT
        H-->>MT: ok (métrica duplicates_total++)
    else novo
        H->>DB: INSERT DailyMovements(...)
        H->>DB: UPSERT DailyBalances do BusinessDate (créditos/débitos)
        H->>DB: Recalcula Opening/Closing de BusinessDate em diante (função de janela)
        H->>DB: UPDATE AccountWatermarks (contiguousSeq, maxSeenSeq, lastEventOccurredOn)
        H->>DB: COMMIT
        H->>R: INCR daily-balance-ver:{merchant}:{account} (best effort)
        H-->>MT: ok
    end
    MT->>MQ: basic.ack (somente após COMMIT)

    Note over MT,MQ: Exceção transitória → retry exponencial em memória (3×)<br/>→ redelivery atrasado (30s, 2min, 10min)<br/>→ fila consolidation...._error + alerta
```

**Pontos de defesa:**
- **ACK depois do COMMIT** — crash entre os dois gera redelivery, absorvido por `ProcessedEvents`.
- **Advisory lock transacional** — liberado automaticamente no `COMMIT/ROLLBACK`; serializa a mesma conta entre réplicas, mantém paralelismo entre contas.
- **Invalidação do cache fora da transação e "best effort"** — se falhar, o TTL curto corrige; o cache nunca bloqueia a projeção.
- **Nenhuma chamada de rede com transação aberta** (Redis é chamado após o commit).

## 4. Fluxo de consulta

```mermaid
sequenceDiagram
    autonumber
    actor C as Cliente
    participant API as Consolidation.Api
    participant CR as CachedDailyBalanceReader
    participant R as Redis
    participant PG as PostgresDailyBalanceReader
    participant DB as postgres-consolidation

    C->>API: GET /api/v1/daily-balances?cashAccountId&from&to
    API->>API: JWT → MerchantId; valida período (≤ 366 dias)
    API->>CR: GetPeriod(merchant, account, from, to)
    CR->>R: GET ver + GET chave(versão)  [timeout 50ms, circuit breaker]
    alt hit
        R-->>CR: payload
    else miss
        CR->>PG: GetPeriod(...)
        PG->>DB: SELECT DailyBalances WHERE (MerchantId, AccountId, BusinessDate BETWEEN)
        PG->>DB: SELECT AccountWatermarks
        PG-->>CR: resultado + consistency
        CR->>R: SET chave EX 60 (best effort)
    else Redis indisponível (breaker aberto)
        CR->>PG: GetPeriod(...) direto
    end
    CR-->>API: resultado
    API-->>C: 200 OK { ..., consistency: { status, lastUpdatedAt, lagSeconds } }
```

## 5. Fluxo de estorno

```mermaid
sequenceDiagram
    autonumber
    actor C as Cliente
    participant API as CashFlow.Api
    participant D as Domain (CashTransaction)
    participant DB as postgres-cashflow

    C->>API: POST /api/v1/transactions/{id}/reversals (Idempotency-Key)
    API->>DB: BEGIN; SELECT original WHERE Id AND MerchantId FOR UPDATE
    alt não existe no tenant
        API-->>C: 404
    else já estornado / é um estorno
        API-->>C: 409 Conflict
    else
        API->>D: original.Reverse(reason, occurredAt) → nova transação tipo inverso
        API->>DB: INSERT CashTransactions (ReversesTransactionId = original)
        Note right of DB: índice único parcial em ReversesTransactionId<br/>garante estorno único mesmo com concorrência
        API->>DB: INSERT OutboxMessages (CashTransactionCreated v1, reversesTransactionId preenchido)
        API->>DB: COMMIT
        API-->>C: 201 Created
    end
```

## 6. Cenário de falha — consolidação indisponível (RNF-01 / RNF-10)

```mermaid
sequenceDiagram
    autonumber
    actor C as Cliente
    participant CF as Cash Flow
    participant MQ as RabbitMQ
    participant DC as Consolidation (fora)

    Note over DC: consumer parado
    loop durante a indisponibilidade
        C->>CF: POST /transactions
        CF-->>C: 201 Created
        CF->>MQ: publish (Outbox)
        Note over MQ: mensagens acumulam na quorum queue (duráveis)
    end
    Note over MQ: alerta: queue depth / lag > 5s
    Note over DC: consumer volta
    MQ->>DC: entrega backlog (prefetch controla vazão)
    DC->>DC: aplica idempotente, recalcula
    Note over DC: status volta para CURRENT quando watermark sem lacunas e lag < SLO
```

## 7. Cenário de falha — RabbitMQ indisponível

```mermaid
sequenceDiagram
    autonumber
    actor C as Cliente
    participant API as CashFlow.Api
    participant DB as postgres-cashflow
    participant W as OutboxPublisher
    participant MQ as RabbitMQ (fora)

    C->>API: POST /transactions
    API->>DB: Tx lançamento + outbox
    API-->>C: 201 Created
    W->>MQ: publish
    MQ--xW: conexão recusada
    W->>DB: RetryCount++, NextAttemptAt com backoff (máx 60s)
    Note over W: métrica outbox.oldest_age_seconds sobe → alerta
    Note over MQ: broker volta
    W->>MQ: publish backlog em ordem de CreatedAt
    MQ-->>W: confirms
    W->>DB: PublishedAt = now()
```
