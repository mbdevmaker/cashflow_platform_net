# ADR-001 — Dois microsserviços em vez de um monolito

**Status:** Aceito · **Data:** 2026-11-23

## Contexto
O desafio exige que o serviço de lançamentos **não fique indisponível** se o consolidado diário cair, e que o consolidado suporte **50 RPS em pico**. As duas capacidades têm criticidade, perfil de carga e modos de falha distintos.

## Decisão
Implementar **dois serviços implantáveis de forma independente**: **Cash Flow Service** (fonte da verdade dos lançamentos) e **Daily Consolidation Service** (projeção de leitura). Cada um tem banco próprio, Clean Architecture interna e containers separados para API e workers.

## Alternativas consideradas
| Alternativa | Motivo da rejeição |
|---|---|
| Monolito modular | Falha, pico ou deploy da consolidação afetaria a escrita; escala acoplada |
| Microsserviços por entidade (contas, lançamentos, saldos, relatórios…) | Fragmentação sem ganho; transações distribuídas; custo operacional alto |
| Serverless (functions) | Cold start, gestão de conexões PG, Outbox menos natural, não roda bem localmente |

## Consequências positivas
- Isolamento de falha e de deploy (RNF-01).
- Escala independente: leitura pode ter N réplicas sem escalar a escrita.
- Fronteiras alinhadas aos bounded contexts (Cash Management × Daily Consolidation).

## Trade-offs
- Mais componentes (2 bancos, broker, workers) → mais operação.
- Consistência eventual entre serviços (ver ADR-008).

## Riscos
- Proliferação futura de serviços sem critério → regra: novo serviço só com contexto, criticidade ou time próprios.

## Quando reavaliar
Se o requisito de disponibilidade independente deixar de existir, um monolito modular com os mesmos módulos e Outbox seria mais simples.
