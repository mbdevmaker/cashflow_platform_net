# ADR-011 — OpenTelemetry como padrão de observabilidade

**Status:** Aceito · **Data:** 2026-11-23

## Contexto
O fluxo de negócio atravessa HTTP, banco, Outbox, broker e consumer. Diagnosticar latência, lag e falhas exige correlação ponta a ponta e métricas de pipeline.

## Decisão
Instrumentar com **OpenTelemetry** (traces, métricas, logs) usando as instrumentações de ASP.NET Core, HttpClient, Npgsql/EF Core, Redis, runtime e MassTransit; exportar via **OTLP** para um **OpenTelemetry Collector**. Localmente: Prometheus, Grafana, Jaeger (ou Tempo) e Loki. `traceparent` gravado na Outbox e propagado na mensagem → **um único trace** do POST até a projeção. Métricas customizadas de negócio/pipeline via `System.Diagnostics.Metrics`.

## Alternativas consideradas
| Alternativa | Avaliação |
|---|---|
| SDK/agente proprietário (Datadog, New Relic, App Insights) | Rápido de adotar, mas lock-in; troca exige reinstrumentação |
| Prometheus + logs em arquivo | Sem tracing distribuído pelo broker |
| .NET Aspire Dashboard | Ótimo para dev (aceita OTLP); não substitui retenção/alertas de produção |

## Consequências positivas
- Neutralidade de vendor (troca de backend = configuração do Collector).
- Correlação logs ↔ traces ↔ métricas por `TraceId`.
- Collector centraliza amostragem, filtragem de PII e roteamento.

## Trade-offs
- Mais componentes no ambiente local.
- Custo de armazenamento de telemetria → tail sampling (100% de erros e lentos, amostra do restante).

## Riscos
- Vazamento de dados sensíveis em atributos/logs → política de atributos permitidos, processor de redaction no Collector, revisão em code review.
