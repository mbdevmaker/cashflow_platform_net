# Runbooks Operacionais — Replay, Reconstrução, Backup e Restore

> Todos os procedimentos que alteram estado exigem **registro de auditoria** (quem, quando, por quê, escopo) e são executados com credenciais operacionais (`ops:*`), não com credenciais de aplicação.

## RB-01 — Mensagens na error queue (`*_error` / `*.dlq`)

**Gatilho:** alerta "Error queue > 0".

1. **Não apagar a fila.** Eventos financeiros não podem ser descartados.
2. Abrir o painel do RabbitMQ (ou `rabbitmqadmin`) e inspecionar os headers `MT-Fault-ExceptionType`, `MT-Fault-Message`, `MT-Reason`, `message_id` (= `eventId`).
3. Localizar o trace pelo `traceparent`/`eventId` no Jaeger/Grafana.
4. Classificar:
   - **Transitória já resolvida** (ex.: banco estava fora) → ir para o passo 5.
   - **Bug no consumer** → corrigir, implantar, ir para o passo 5.
   - **Contrato inválido do produtor** → abrir incidente no Cash Flow; corrigir; republicar via RB-03 (com o mesmo `transactionId`).
5. **Replay:** mover mensagens de `_error` para a fila de origem (MassTransit: `move` via shovel/`rabbitmqadmin` ou ferramenta de ops `ops-replay`). Idempotência garante segurança.
6. Confirmar: `_error = 0`, projeção da conta afetada com status `CURRENT`.
7. Registrar o incidente (causa, eventos afetados, ação).

## RB-02 — Backlog crescente / lag acima do SLO

1. Dashboard 2: o gargalo está na **Outbox** (oldest age alto) ou na **fila** (depth alto)?
2. Outbox: verificar conectividade com o broker, logs do publisher, réplicas do publisher.
3. Fila: verificar consumer (réplicas, erros, duração); escalar (`docker compose up --scale daily-consolidation-consumer=3` / KEDA); verificar contenção de lock (contas quentes) e latência do PG de consolidação.
4. Confirmar drenagem e retorno de `lag_seconds` ao SLO.

## RB-03 — Reconstrução/reconciliação da projeção (RF-DC-10)

**Opção A — a partir de `daily_movements` (preferida, sem envolver o produtor):**
1. Ativar modo manutenção da conta/escopo (status `PROCESSING` exposto).
2. Para cada `(merchant, account, currency)` do escopo: executar o recálculo a partir da menor `business_date` (mesma função usada pelo consumer, com advisory lock).
3. Invalidar cache (`INCR` da versão das contas).
4. Validar com a reconciliação (opção C).

**Opção B — republicação a partir do Cash Flow (projeção ou `daily_movements` comprometidos):**
1. Chamar o endpoint operacional do Cash Flow `POST /ops/v1/republish` (`ops:replay`), com escopo (`merchantId`, `cashAccountId`, período). Ele insere na Outbox os eventos das transações do escopo **com os mesmos `eventId` determinísticos** (derivados do `transactionId`).
2. Na consolidação, para reprocessar do zero o escopo: remover `processed_events`, `daily_movements` e `daily_balances` do escopo **em transação**, com a conta bloqueada, antes da republicação.
3. Acompanhar drenagem; validar com a opção C.

**Opção C — reconciliação (detecção):**
- Job compara, por conta e dia, contagem e somas do Cash Flow (via endpoint de ops de agregados) com `daily_balances`. Divergências → alerta + relatório. Não acessa o banco do outro serviço diretamente (regra 20).

## RB-04 — RabbitMQ indisponível

1. Confirmar: escrita continua `201` (dashboard 1) e Outbox crescendo (dashboard 2).
2. Restaurar o broker (local: `docker compose start rabbitmq`; produção: failover do cluster/serviço gerenciado).
3. Publisher reconecta sozinho e drena. Monitorar `oldest_age_seconds` voltar a ~0.
4. Se a indisponibilidade for longa: verificar espaço em disco do PG Cash Flow.

## RB-05 — Backup e restore

| Banco | Backup | Restore |
|---|---|---|
| Cash Flow (autoritativo) | Produção: backup contínuo + PITR (35 dias). Local: `docker compose exec postgres-cashflow pg_dump -Fc -U $CF_DB_USER cashflow > backup/cashflow.dump` | `pg_restore -d cashflow --clean backup/cashflow.dump`; após restore PITR, eventos já publicados após o ponto restaurado podem ter sido consumidos → executar **RB-03 opção B** para alinhar a projeção |
| Consolidation (derivado) | Diário + PITR | Restore **ou** reconstrução completa via RB-03 opção B (não depende de backup) |
| RabbitMQ | `definitions.json` versionado; mensagens não são "backup" (Outbox e Cash Flow são a fonte) | Recriar topologia via definitions; republicar se necessário |
| Redis | Sem backup (descartável) | Sobe vazio |

**Teste de restore:** executar trimestralmente em ambiente isolado; registrar tempo (RTO real) e ponto (RPO real).

## RB-06 — Migrations

1. Pipeline executa o job de migration (bundle EF) **antes** do rollout.
2. Mudanças sempre **expand/contract**: adicionar coluna nullable → deploy → backfill → tornar obrigatória em release posterior. Nunca remover/renomear em um único passo.
3. Rollback de aplicação não exige rollback de schema (compatibilidade com N-1).
