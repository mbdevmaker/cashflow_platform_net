# ADR-004 — PostgreSQL por serviço

**Status:** Aceito · **Data:** 2026-11-23

## Contexto
Os dois serviços precisam de persistência transacional, valores decimais exatos, concorrência controlada e isolamento de falhas.

## Decisão
**PostgreSQL 17**, com **um banco lógico por serviço** (instâncias separadas no Compose e em produção). Acesso via EF Core + Npgsql; migrations versionadas e executadas por job controlado.

## Alternativas consideradas
| Alternativa | Prós | Contras |
|---|---|---|
| SQL Server | Maduro, ótimo tooling .NET | Licença comercial; custo gerenciado maior |
| MongoDB | Flexível | Transações multi-documento mais caras; sem FK composta/RLS; decimal menos natural |
| EventStoreDB/KurrentDB | Natural para Event Sourcing | Event Sourcing está fora do escopo |
| Um único banco compartilhado (schemas separados) | Menos infraestrutura | Falha/manutenção compartilhada; tentação de JOIN entre contextos |

## Consequências positivas
- ACID para Outbox e idempotência; `NUMERIC(19,4)`; `SKIP LOCKED`; advisory locks; `ON CONFLICT`; RLS; funções de janela.
- Sem custo de licença; gerenciado com HA em todas as clouds.
- Falha do banco de consolidação não afeta a escrita.

## Trade-offs
- Dois bancos para operar, fazer backup e monitorar.
- Sem consultas cruzadas (intencional).

## Riscos
- Esgotamento de conexões com muitas réplicas → PgBouncer em produção, pools dimensionados, métrica de *pool wait*.
