# ADR-010 — Estorno compensatório e imutabilidade financeira

**Status:** Aceito · **Data:** 2026-11-23

## Contexto
Lançamentos financeiros precisam de trilha de auditoria completa. Edição/exclusão destrói histórico e complica a consolidação por eventos.

## Decisão
Lançamentos confirmados são **imutáveis** (sem `UPDATE`/`DELETE` — reforçado por permissões de banco). Correções por **estorno integral compensatório**: nova transação com tipo inverso e `reverses_transaction_id` apontando para a original. **Único estorno por transação** (índice único parcial). Publicado como o mesmo evento canônico `CashTransactionCreated` com `reversesTransactionId` preenchido (SPEC §9.2).

## Alternativas consideradas
| Alternativa | Motivo da rejeição |
|---|---|
| Editar/apagar lançamento | Perde auditoria; exige eventos de update/delete e lógica de "desfazer" no consumidor |
| Evento separado `CashTransactionReversed` | Mais handlers e contratos; o estorno é contabilmente apenas uma movimentação inversa |
| Ledger de partidas dobradas | Correto para contabilidade formal; fora do escopo v1 |

## Consequências positivas
- Auditoria completa; consumidor simples (um único handler).
- Saldo sempre reproduzível a partir do histórico.

## Trade-offs
- Estorno parcial não suportado na v1.
- Data do estorno (`occurredAt` do estorno) pode diferir da original → afeta o dia do estorno, não o original (comportamento contábil esperado; documentado).

## Riscos
- Estornos concorrentes → `SELECT ... FOR UPDATE` na original + índice único parcial.
