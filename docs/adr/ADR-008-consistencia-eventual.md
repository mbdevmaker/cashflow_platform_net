# ADR-008 — Consistência eventual no consolidado

**Status:** Aceito · **Data:** 2026-11-23

## Contexto
A independência entre serviços (ADR-001/002) implica que o consolidado é atualizado após o commit do lançamento.

## Decisão
Aceitar **consistência eventual** com **SLO inicial de 5 s** (p95, carga nominal) entre o commit do lançamento e a projeção atualizada. **Tornar a defasagem visível**: toda resposta da API de consolidação inclui `consistency.status` (`CURRENT`, `PROCESSING`, `DELAYED`, `UNKNOWN`), `lastUpdatedAt` e `lagSeconds`, calculados por watermark (ADR-013).

## Alternativas consideradas
| Alternativa | Motivo da rejeição |
|---|---|
| Consistência forte (atualizar saldo na mesma transação do lançamento) | Exige consolidação no mesmo banco/processo → anula ADR-001 |
| Esconder a defasagem | Usuário pode tomar decisão com dado desatualizado sem saber |

## Consequências positivas
- Disponibilidade da escrita independente da leitura.
- Transparência para o usuário e para a operação (SLO mensurável).

## Trade-offs
- Janela de segundos em que o saldo não reflete o último lançamento.

## Riscos
- Usuário que precisa *read-your-writes* → evolução: endpoint de saldo do dia corrente calculado no Cash Flow, ou cliente aguardar `status=CURRENT`.
