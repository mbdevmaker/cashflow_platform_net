# Requisitos Funcionais e Não Funcionais (refinados)

> Refinamento do enunciado do desafio e do [SPEC.md](../../SPEC.md). Itens marcados como **(premissa)** são hipóteses de projeto a validar com o negócio.

## 1. Requisitos do enunciado → refinamento

| Enunciado | Refinamento |
|---|---|
| "Serviço que faça o controle de lançamentos" | Cash Flow Service: contas, créditos, débitos, abertura, estorno, idempotência, auditoria, eventos |
| "Serviço do consolidado diário" | Daily Consolidation Service: projeção por comerciante/conta/data/moeda, consultas por dia/período, status de defasagem, reconstrução |
| "Lançamentos não devem ficar indisponíveis se o consolidado cair" | RNF-01 + comunicação assíncrona + Outbox; testado por cenário de resiliência |
| "50 RPS com no máximo 5% de perda" | RNF-02/03: **taxa de falha HTTP ≤ 5% na API de consolidação** em 50 RPS sustentados (15 min) e em pico; **não** se aplica a lançamentos (zero perda — RNF-06) |

## 2. Requisitos funcionais

### Cash Flow
| ID | Requisito | Critério de aceite |
|---|---|---|
| RF-CF-01 | Criar conta de caixa vinculada ao comerciante autenticado | `201`; moeda ISO 4217 (v1: BRL); fuso IANA válido; `MerchantId` do token |
| RF-CF-02 | Registrar crédito | `201`; `amount > 0`; até 4 casas decimais; `businessDate` calculada |
| RF-CF-03 | Registrar débito | Idem; saldo negativo permitido |
| RF-CF-04 | Consultar lançamento por id | `200`; `404` se não existir **ou** for de outro tenant |
| RF-CF-05 | Listar por conta e período, paginado | `pageSize ≤ 100`; ordenação estável `(occurredAt, id)`; metadados de paginação |
| RF-CF-06 | Idempotência | Mesma chave + payload → mesmo resultado; payload diferente → `409` |
| RF-CF-07 | Estorno integral compensatório | Novo lançamento inverso; original intacto; segundo estorno → `409` |
| RF-CF-08 | Registrar `occurredAt` e `createdAt` | Ambos persistidos em UTC |
| RF-CF-09 | Outbox na mesma transação | Teste de atomicidade |
| RF-CF-10 | Health checks | `/health/live`, `/health/ready` |
| RF-CF-11 **(premissa)** | Saldo de abertura | Lançamento `OPENING_BALANCE`, no máximo um por conta |

### Daily Consolidation
| ID | Requisito | Critério de aceite |
|---|---|---|
| RF-DC-01 | Consumir lançamentos criados/estornados | Um contrato único `CashTransactionCreated v1` |
| RF-DC-02 | Idempotência por `EventId` | Duplicata não altera saldos |
| RF-DC-03 | Consolidar por `MerchantId, CashAccountId, BusinessDate, Currency` | PK da projeção |
| RF-DC-04 | Créditos, débitos, abertura, fechamento | `Closing = Opening + Credits − Debits`; `Opening(d) = Closing(d−1)` |
| RF-DC-05 | Consolidado de um dia | Dia sem movimento retorna abertura = fechamento do dia anterior |
| RF-DC-06 | Consolidado por período | Período máximo **(premissa)**: 366 dias |
| RF-DC-07 | Timestamp e estado de defasagem | `consistency.status/lastUpdatedAt/lagSeconds` |
| RF-DC-08 | Recuperação após indisponibilidade | Backlog drenado sem intervenção |
| RF-DC-09 | Health checks | Idem |
| RF-DC-10 | Reconstrução/reconciliação | Runbook + endpoint operacional protegido |

## 3. Requisitos não funcionais (com métricas)

| ID | Atributo | Meta | Métrica / como medir |
|---|---|---|---|
| RNF-01 | Independência | Escrita OK com consolidação fora | Teste de resiliência #1 |
| RNF-02 | Throughput de consulta | 50 RPS sustentados 15 min + pico 5 min | k6 — `http_reqs` |
| RNF-03 | Taxa de sucesso | ≥ 95% de 2xx (meta interna: ≥ 99,5%) | k6 — `http_req_failed` |
| RNF-04 | Atualização eventual | p95 ≤ 5 s commit → projeção | `consolidation.lag_seconds` |
| RNF-05 | Latência | p95 ≤ 300 ms consultas | k6 + histogramas OTel |
| RNF-06 | Integridade | 0 lançamentos confirmados perdidos | Reconciliação contagem Cash Flow × Consolidation |
| RNF-07 | Escalabilidade | APIs stateless; consumers horizontais | Teste com N réplicas |
| RNF-08 | Segurança | 0 acessos cross-tenant | Testes negativos no CI |
| RNF-09 | Observabilidade | Logs, métricas, traces correlacionados | Dashboards provisionados |
| RNF-10 | Recuperação | Backlog drenado automaticamente | Teste de resiliência #1/#2 |
| RNF-11 **(premissa)** | Disponibilidade (produção) | Cash Flow 99,95%; Consolidation 99,9% | SLO/error budget |
| RNF-12 **(premissa)** | Recuperação de desastre | Cash Flow RPO 0 (AZ) / RTO ≤ 5 min | Testes de failover |
| RNF-13 | Manutenibilidade | Regras de dependência verificadas | Testes de arquitetura |
| RNF-14 | Portabilidade | Sobe com `docker compose up --build` | CI/README |

## 4. Restrições

- .NET 10, PostgreSQL, RabbitMQ, MassTransit, Redis, OpenTelemetry, Docker Compose (SPEC §2).
- Sem Kubernetes, Kafka, Event Sourcing, Saga, ledger de partidas dobradas, multi-moeda na v1.
- Sem `float/double` para dinheiro; sem chamadas HTTP síncronas entre serviços.
