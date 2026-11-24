# Architecture Decision Records (ADRs)

Formato: **Contexto · Decisão · Alternativas consideradas · Consequências positivas · Trade-offs · Riscos · Quando reavaliar**. Status possíveis: *Proposto*, *Aceito*, *Substituído por ADR-xxx*, *Obsoleto*.

| ADR | Título | Status |
|---|---|---|
| [ADR-001](ADR-001-dois-microsservicos.md) | Dois microsserviços em vez de um monolito | Aceito |
| [ADR-002](ADR-002-event-driven.md) | Event-driven para desacoplar lançamentos e consolidação | Aceito |
| [ADR-003](ADR-003-cqrs-projecao-leitura.md) | CQRS com projeção de leitura | Aceito |
| [ADR-004](ADR-004-postgresql-por-servico.md) | PostgreSQL por serviço | Aceito |
| [ADR-005](ADR-005-rabbitmq-masstransit.md) | RabbitMQ + MassTransit | Aceito |
| [ADR-006](ADR-006-outbox-idempotent-consumer.md) | Transactional Outbox e Idempotent Consumer | Aceito |
| [ADR-007](ADR-007-multi-tenancy-merchantid.md) | Multi-tenancy lógica por MerchantId | Aceito |
| [ADR-008](ADR-008-consistencia-eventual.md) | Consistência eventual no consolidado | Aceito |
| [ADR-009](ADR-009-redis-cache-nao-autoritativo.md) | Redis como cache não autoritativo | Aceito |
| [ADR-010](ADR-010-estorno-compensatorio.md) | Estorno compensatório e imutabilidade financeira | Aceito |
| [ADR-011](ADR-011-opentelemetry.md) | OpenTelemetry como padrão de observabilidade | Aceito |
| [ADR-012](ADR-012-docker-compose-local-kubernetes-alvo.md) | Docker Compose local; Kubernetes como plataforma-alvo | Aceito |
| [ADR-013](ADR-013-ordenacao-concorrencia-por-conta.md) | Concorrência por conta com advisory lock e watermark por sequência | Aceito |
