# Visão Geral — Modelo C4

> Diagramas em Mermaid (renderizam no GitHub). Níveis: **Contexto (C1)** → **Container (C2)** → **Componente (C3)**.

## C1 — Contexto do sistema

```mermaid
C4Context
    title C1 — Plataforma Multiempresa de Fluxo de Caixa

    Person(op, "Operador financeiro", "Registra e consulta lançamentos")
    Person(cons, "Consultor financeiro", "Consulta saldos consolidados")
    Person(adm, "Administrador do comerciante", "Gerencia contas e usuários do comerciante")
    Person(plat, "Administrador da plataforma", "Operação técnica; sem acesso irrestrito a dados financeiros")

    System(cfp, "Plataforma de Fluxo de Caixa", "Registra créditos/débitos e fornece saldo diário consolidado por comerciante")

    System_Ext(idp, "Identity Provider (OIDC)", "Autentica usuários e emite JWT com merchant_id e roles")
    System_Ext(obs, "Stack de Observabilidade", "Métricas, traces, logs e alertas")
    System_Ext(legacy, "Sistema legado (transição)", "ERP/planilhas atuais do comerciante — opcional")

    Rel(op, cfp, "Registra lançamentos", "HTTPS/JSON")
    Rel(cons, cfp, "Consulta consolidado", "HTTPS/JSON")
    Rel(adm, cfp, "Cria contas de caixa", "HTTPS/JSON")
    Rel(op, idp, "Autentica", "OIDC")
    Rel(cfp, idp, "Valida tokens (JWKS)", "HTTPS")
    Rel(cfp, obs, "Exporta telemetria", "OTLP")
    Rel(plat, obs, "Monitora e opera")
    Rel(legacy, cfp, "Migração gradual (CDC/ACL)", "Eventos")
```

## C2 — Containers

```mermaid
C4Container
    title C2 — Containers

    Person(user, "Usuário do comerciante")

    System_Boundary(b1, "Cash Flow Service (bounded context: Cash Management)") {
        Container(cfapi, "CashFlow.Api", ".NET 10 / ASP.NET Core", "REST: contas, lançamentos, estornos. Autenticação, tenant, idempotência")
        Container(cfworker, "CashFlow.OutboxPublisher", ".NET 10 Worker", "Claim SKIP LOCKED da Outbox e publish com confirms")
        ContainerDb(cfdb, "postgres-cashflow", "PostgreSQL 17", "CashAccounts, CashTransactions, IdempotencyRecords, OutboxMessages")
    }

    System_Boundary(b2, "Daily Consolidation Service (bounded context: Daily Financial Consolidation)") {
        Container(dcconsumer, "Consolidation.Consumer", ".NET 10 Worker + MassTransit", "Consome eventos, idempotência, recalcula saldos")
        Container(dcapi, "Consolidation.Api", ".NET 10 / ASP.NET Core", "REST somente leitura: saldo diário, período, status")
        ContainerDb(dcdb, "postgres-consolidation", "PostgreSQL 17", "DailyBalances, DailyMovements, ProcessedEvents, AccountWatermarks")
        ContainerDb(redis, "redis", "Redis 8 / Valkey", "Cache-aside descartável")
    }

    Container(mq, "rabbitmq", "RabbitMQ 4.x", "Exchange cashflow.events, quorum queues, DLQ")
    Container(otel, "otel-collector", "OpenTelemetry Collector", "Recebe OTLP; exporta para Prometheus/Jaeger/Loki")
    System_Ext(idp, "Identity Provider", "OIDC/JWT")

    Rel(user, cfapi, "POST/GET", "HTTPS")
    Rel(user, dcapi, "GET", "HTTPS")
    Rel(cfapi, cfdb, "Tx: lançamento + outbox", "Npgsql")
    Rel(cfworker, cfdb, "Claim/marcar publicado", "Npgsql")
    Rel(cfworker, mq, "Publish CashTransactionCreated v1", "AMQP 0-9-1 + confirms")
    Rel(mq, dcconsumer, "Entrega at-least-once", "AMQP")
    Rel(dcconsumer, dcdb, "Tx: ProcessedEvents + projeção", "Npgsql")
    Rel(dcconsumer, redis, "Invalida versão da conta", "RESP")
    Rel(dcapi, redis, "Cache-aside", "RESP")
    Rel(dcapi, dcdb, "Fallback / miss", "Npgsql")
    Rel(cfapi, idp, "JWKS", "HTTPS")
    Rel(dcapi, idp, "JWKS", "HTTPS")
    Rel(cfapi, otel, "OTLP")
    Rel(dcconsumer, otel, "OTLP")
```

### Responsabilidades e independência

| Container | Escala | Pode cair sem afetar a escrita? | Estado |
|---|---|---|---|
| CashFlow.Api | Horizontal (stateless) | — (é a escrita) | Nenhum (stateless) |
| CashFlow.OutboxPublisher | Horizontal (SKIP LOCKED) | ✅ Outbox acumula | Nenhum |
| postgres-cashflow | Vertical + standby HA | ❌ fonte da verdade | **Autoritativo** |
| rabbitmq | Cluster 3 nós (prod) | ✅ Outbox acumula | Durável (filas) |
| Consolidation.Consumer | Horizontal (lock por conta) | ✅ fila acumula | Nenhum |
| Consolidation.Api | Horizontal (stateless) | ✅ | Nenhum |
| postgres-consolidation | Vertical + réplica de leitura | ✅ | Projeção (reconstruível) |
| redis | Réplica | ✅ (e não afeta nem a leitura) | Descartável |

## C3 — Componentes do Cash Flow Service

```mermaid
flowchart TB
    subgraph Api[CashFlow.Api]
        EP[Minimal API Endpoints<br/>/api/v1/cash-accounts<br/>/api/v1/transactions]
        AUTH[JWT Bearer + Policies<br/>transactions:write/read]
        TEN[TenantResolutionMiddleware<br/>→ ITenantContext]
        PD[ProblemDetails / Exception Handler]
        RL[Rate Limiter por tenant]
    end

    subgraph App[CashFlow.Application]
        H1[CreateCashAccountHandler]
        H2[RegisterTransactionHandler]
        H3[ReverseTransactionHandler]
        Q1[GetTransactionQuery / ListTransactionsQuery]
        DEC[Decorators: Validation · Logging · Transaction · Metrics]
        PORTS[[Portas: ICashAccountRepository · ICashTransactionRepository<br/>IIdempotencyStore · IOutboxWriter · IUnitOfWork · IClock · ITenantContext]]
    end

    subgraph Dom[CashFlow.Domain]
        AGG1[CashAccount]
        AGG2[CashTransaction]
        VO[Money · Currency · BusinessDate · IanaTimeZone · IdempotencyKey]
        SVC[BusinessDateCalculator]
        EVT[Domain Events: TransactionRegistered]
    end

    subgraph Infra[CashFlow.Infrastructure]
        EF[CashFlowDbContext EF Core + Npgsql]
        REPO[Repositórios]
        OBX[OutboxWriter: domain event → contrato v1 → OutboxMessages]
        PUB[OutboxPublisherService: SKIP LOCKED + MassTransit confirms]
        TEL[Métricas outbox.pending / oldest_age]
    end

    EP --> AUTH --> TEN --> DEC --> H1 & H2 & H3 & Q1
    H2 --> AGG1 & AGG2
    H2 --> PORTS
    PORTS -. implementado por .-> REPO & OBX
    REPO --> EF
    OBX --> EF
    PUB --> EF
```

## C3 — Componentes do Daily Consolidation Service

```mermaid
flowchart TB
    subgraph Consumer[Consolidation.Consumer]
        MT[MassTransit Receive Endpoint<br/>consolidation.cash-transaction-created<br/>retry → redelivery → _error]
        CA[CashTransactionCreatedConsumer<br/>adaptador fino]
    end

    subgraph App[Consolidation.Application]
        H[ApplyCashMovementHandler]
        Q[GetDailyBalanceQuery · GetPeriodBalanceQuery · GetProjectionStatusQuery]
        PORTS[[Portas: IProjectionStore · IProcessedEventStore · IAccountLock<br/>IDailyBalanceReader · ICacheInvalidator · IClock]]
    end

    subgraph Dom[Consolidation.Domain]
        P[DailyBalanceProjection<br/>recalcula a partir de BusinessDate]
        WM[AccountWatermark<br/>sequência contígua / lacunas]
        CS[ConsistencyStatusPolicy<br/>CURRENT · PROCESSING · DELAYED · UNKNOWN]
    end

    subgraph Infra[Consolidation.Infrastructure]
        EF[ConsolidationDbContext]
        LOCK[PgAdvisoryAccountLock]
        PG[PostgresDailyBalanceReader]
        CACHE[CachedDailyBalanceReader<br/>decorator HybridCache/Redis + circuit breaker]
        INV[RedisVersionInvalidator]
    end

    subgraph Api[Consolidation.Api]
        EP[GET /api/v1/daily-balances...]
    end

    MT --> CA --> H --> P & WM
    H --> PORTS
    EP --> Q --> PORTS
    PORTS -. implementado por .-> LOCK & EF & CACHE & INV
    CACHE --> PG
```

## Mapeamento contexto → container → dados

| Bounded context | Containers | Dados autoritativos | Dados derivados |
|---|---|---|---|
| Cash Management | CashFlow.Api, CashFlow.OutboxPublisher | CashAccounts, CashTransactions, IdempotencyRecords | OutboxMessages (transitório) |
| Daily Financial Consolidation | Consolidation.Consumer, Consolidation.Api | ProcessedEvents, AccountWatermarks | DailyMovements, DailyBalances, cache Redis |
| Identity & Access (externo) | IdP | Usuários, associações usuário-comerciante | Claims no JWT |
