# ADR-006 — Transactional Outbox e Idempotent Consumer

**Status:** Aceito · **Data:** 2026-11-23

## Contexto
Persistir o lançamento e publicar o evento são operações em sistemas diferentes (*dual-write*). Sem coordenação, podemos ter lançamento sem evento (perda) ou evento sem lançamento (fantasma). O broker entrega at-least-once, então duplicatas são esperadas.

## Decisão
**Produtor:** tabela `outbox_messages` gravada **na mesma transação** do lançamento. Worker separado faz claim com `FOR UPDATE SKIP LOCKED`, publica com publisher confirms e marca `published_at` só após confirmação. Backoff exponencial, sem descarte.

**Consumidor:** na mesma transação do banco de consolidação: advisory lock por conta → `INSERT processed_events (event_id, consumer_name) ON CONFLICT DO NOTHING` → se novo, aplica movimento e recalcula → `COMMIT` → **ACK**.

Outbox **própria** (não a do MassTransit) para controlar schema, métricas e reduzir dependência da biblioteca (ver risco de licença no ADR-005).

## Alternativas consideradas
| Alternativa | Motivo da rejeição |
|---|---|
| Publicar após `SaveChanges` | Perde eventos em crash |
| 2PC/XA | Não suportado de forma prática pelo RabbitMQ; frágil e bloqueante |
| Outbox do MassTransit (`AddEntityFrameworkOutbox`) | Válida; rejeitada por acoplamento à biblioteca e controle de métricas |
| Event Sourcing | Fora de escopo |
| Deduplicação no broker | Não confiável no RabbitMQ; idempotência deve estar junto do efeito |

## Consequências positivas
- **Zero perda** de lançamento confirmado (RNF-06).
- Efeito **exactly-once** no estado com entrega at-least-once.
- Escrita funciona com broker fora.

## Trade-offs
- Latência adicional (polling da Outbox: 250–500 ms; mitigável com `LISTEN/NOTIFY`).
- Tabela `processed_events` cresce → retenção por janela segura (ex.: 90 dias) maior que qualquer redelivery/replay possível; replays mais antigos usam o `transaction_id` único em `daily_movements` como segunda barreira.

## Riscos
- Outbox crescendo durante indisponibilidade prolongada → alerta por idade e por contagem; capacidade de disco dimensionada.
