# ADR-003 — CQRS com projeção de leitura

**Status:** Aceito · **Data:** 2026-11-23

## Contexto
O saldo diário exige somas por data e saldo acumulado. Calcular na hora da consulta, sobre os lançamentos, custaria O(lançamentos) por requisição e competiria com a escrita.

## Decisão
Separar **modelo de escrita** (Cash Flow: lançamentos imutáveis) do **modelo de leitura** (Consolidation: `DailyBalances` pré-calculado por `MerchantId, CashAccountId, BusinessDate, Currency`). Dentro de cada serviço, comandos passam pelo domínio; consultas usam *read stores* otimizados (projeções/DTOs, `AsNoTracking`).

**Não** adotamos Event Sourcing: a fonte da verdade são as linhas imutáveis de `cash_transactions`.

## Alternativas consideradas
| Alternativa | Motivo da rejeição |
|---|---|
| Consulta agregada on-the-fly no banco de escrita | Concorre com a escrita, latência cresce com o histórico, acopla serviços |
| View materializada no banco do Cash Flow | Refresh custoso; acopla a consolidação ao banco do produtor |
| CQRS + Event Sourcing | Complexidade desproporcional (snapshots, upcasting, rebuilds); fora do escopo v1 |

## Consequências positivas
- Consulta O(dias do período) por PK — atende p95 ≤ 300 ms com folga.
- Leitura escala independentemente (réplicas de leitura, cache).
- Projeção **reconstruível** a partir de `DailyMovements` ou republicação de eventos.

## Trade-offs
- Duplicação controlada de dados.
- Recálculo em eventos retroativos.

## Riscos
- Projeção divergente por bug → reconciliação/rebuild documentados (runbooks).
