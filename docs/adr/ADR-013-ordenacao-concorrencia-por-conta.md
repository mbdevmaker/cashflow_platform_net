# ADR-013 — Concorrência por conta com advisory lock e watermark por sequência

**Status:** Aceito · **Data:** 2026-11-23

## Contexto
O RabbitMQ não garante ordem com consumidores concorrentes; eventos podem chegar duplicados, fora de ordem ou retroativos. O status `CURRENT` deve ser baseado em posição de processamento conhecida, não em `UpdatedAt` (SPEC §8.3, §10.2).

## Decisão
1. **Projeção order-agnostic:** totais diários são somas (comutativas); evento retroativo recalcula o dia e os posteriores.
2. **Serialização por conta entre réplicas:** `pg_advisory_xact_lock(hash(merchantId, cashAccountId))` na transação do consumer. Paralelismo total entre contas.
3. **Opcional:** `UsePartitioner` do MassTransit por `CashAccountId` para reduzir contenção dentro de cada réplica.
4. **Watermark por sequência:** o Cash Flow atribui `accountSequence` monotônico e sem lacunas por conta (incremento em `cash_accounts.last_sequence` na transação do lançamento). O evento v1 inclui `accountSequence` como **campo opcional aditivo**. A consolidação mantém `contiguous_sequence` e `max_seen_sequence` por conta.

## Alternativas consideradas
| Alternativa | Avaliação |
|---|---|
| Single Active Consumer (uma fila, um consumidor) | Ordem forte, mas sem escala horizontal |
| Consistent-hash exchange + fila por partição + SAC | Ordem por conta e escala; mais topologia para operar — evolução se a contenção de lock for medida como problema |
| Kafka com partição por conta | Ordem por partição; ver ADR-005 |
| Lock otimista (versão da linha) | Muitos conflitos/retries em contas quentes |
| Usar UUIDv7 como ordem | Proibido pelo SPEC; relógios distintos e lacunas indetectáveis |

## Consequências positivas
- Correção sob duplicidade/desordem; escala entre contas.
- Status de consistência honesto (detecta lacunas).

## Trade-offs
- Contas muito ativas serializam (throughput por conta limitado ao tempo de transação).
- `last_sequence` cria um ponto de serialização por conta na escrita (row lock curto) — aceitável para o volume de um comerciante.

## Riscos
- Recálculo longo em contas com muito histórico → métrica `recalculated_days`; snapshots como evolução.
