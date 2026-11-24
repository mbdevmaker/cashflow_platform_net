# ADR-002 — Event-driven para desacoplar lançamentos e consolidação

**Status:** Aceito · **Data:** 2026-11-23

## Contexto
A consolidação precisa conhecer cada lançamento confirmado. Uma integração síncrona colocaria a consolidação no caminho crítico da escrita.

## Decisão
O Cash Flow publica o evento de integração **`CashTransactionCreated` v1** (contrato versionado em JSON) via RabbitMQ. A consolidação consome de forma assíncrona. **Não há chamadas HTTP entre os serviços.**

## Alternativas consideradas
| Alternativa | Motivo da rejeição |
|---|---|
| HTTP/gRPC síncrono Cash Flow → Consolidation | Acopla disponibilidade; viola RNF-01 |
| Polling da consolidação na API do Cash Flow | Latência, carga na fonte da verdade, paginação frágil |
| Banco compartilhado / leitura direta do banco do Cash Flow | Acoplamento de schema; viola a regra 20 do SPEC |
| CDC (Debezium) como integração principal | Expõe schema interno como contrato; exige Kafka Connect/Debezium Server. Reservado para a migração do legado |

## Consequências positivas
- Escrita independente da consolidação e do próprio broker (com Outbox — ADR-006).
- Novos consumidores (ex.: conciliação, analytics, notificações) sem alterar o produtor.
- Contrato explícito e versionado entre bounded contexts.

## Trade-offs
- Consistência eventual (ADR-008).
- Necessidade de idempotência e tolerância à desordem no consumidor.
- Depuração distribuída → mitigada por tracing (ADR-011).

## Riscos
- Evolução do contrato quebrar consumidores → regras de compatibilidade: apenas campos opcionais em v1; mudanças incompatíveis = nova versão publicada em paralelo; testes de contrato no CI.
