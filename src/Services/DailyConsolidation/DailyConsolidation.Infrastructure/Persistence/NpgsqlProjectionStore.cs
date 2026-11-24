using DailyConsolidation.Application;
using DailyConsolidation.Domain;
using Npgsql;
using NpgsqlTypes;

namespace DailyConsolidation.Infrastructure.Persistence;

internal sealed class NpgsqlProjectionStore(NpgsqlDataSource dataSource) : IProjectionStore
{
    private static readonly string InstanceId = $"{Environment.MachineName}:{Environment.ProcessId}";

    public async Task<ProjectionResult> ApplyAsync(Movement m, string consumerName, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Serialização por conta entre réplicas (ADR-013); contas diferentes processam em paralelo.
        await LockAccountAsync(conn, tx, m.MerchantId, m.CashAccountId, ct);

        // Idempotência transacional por EventId.
        var inserted = await ExecAsync(conn, tx, """
            INSERT INTO processed_events (event_id, consumer_name, processed_at)
            VALUES (@id, @consumer, now()) ON CONFLICT DO NOTHING
            """, ct, ("id", m.EventId), ("consumer", consumerName));
        if (inserted == 0)
        {
            await tx.CommitAsync(ct);
            return new ProjectionResult(ProjectionOutcome.Duplicate, 0);
        }

        // Defesa: conta já conhecida com outro comerciante ou outra moeda → evento inválido (vai para error queue).
        await using (var check = new NpgsqlCommand("""
            SELECT merchant_id, currency FROM daily_movements WHERE cash_account_id = @a LIMIT 1
            """, conn, tx))
        {
            check.Parameters.AddWithValue("a", m.CashAccountId);
            await using var r = await check.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                if (r.GetGuid(0) != m.MerchantId)
                    throw new InvalidEventException("cashAccountId pertence a outro comerciante.");
                if (!string.Equals(r.GetString(1), m.Currency, StringComparison.Ordinal))
                    throw new InvalidEventException("Conta não pode misturar moedas.");
            }
        }

        var movementInserted = await ExecAsync(conn, tx, """
            INSERT INTO daily_movements (event_id, merchant_id, cash_account_id, transaction_id, business_date, currency,
                                         transaction_type, amount, reverses_transaction_id, account_sequence, occurred_on, applied_at)
            VALUES (@e, @m, @a, @t, @d, @c, @type, @amount, @rev, @seq, @o, now())
            ON CONFLICT (transaction_id) DO NOTHING
            """, ct,
            ("e", m.EventId), ("m", m.MerchantId), ("a", m.CashAccountId), ("t", m.TransactionId), ("d", m.BusinessDate),
            ("c", m.Currency), ("type", m.TransactionType), ("amount", m.Amount),
            ("rev", (object?)m.ReversesTransactionId ?? DBNull.Value), ("seq", (object?)m.AccountSequence ?? DBNull.Value),
            ("o", m.OccurredOn));

        if (movementInserted == 0)
        {
            // Mesma transação reentregue com outro EventId (ex.: republicação operacional): efeito já aplicado.
            await tx.CommitAsync(ct);
            return new ProjectionResult(ProjectionOutcome.Duplicate, 0);
        }

        var recalculated = await RecalculateFromAsync(conn, tx, m.MerchantId, m.CashAccountId, m.Currency, m.BusinessDate, ct);
        await UpdateWatermarkAsync(conn, tx, m, ct);

        await tx.CommitAsync(ct); // o ACK ao broker só ocorre depois deste commit (consumer retorna após ApplyAsync)
        return new ProjectionResult(ProjectionOutcome.Applied, recalculated);
    }

    private static async Task<int> RecalculateFromAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid merchantId, Guid accountId, string currency, DateOnly fromDate, CancellationToken ct)
    {
        decimal previousClosing = 0m;
        await using (var prev = new NpgsqlCommand("""
            SELECT closing_balance FROM daily_balances
             WHERE merchant_id = @m AND cash_account_id = @a AND currency = @c AND business_date < @d
             ORDER BY business_date DESC LIMIT 1
            """, conn, tx))
        {
            prev.Parameters.AddWithValue("m", merchantId);
            prev.Parameters.AddWithValue("a", accountId);
            prev.Parameters.AddWithValue("c", currency);
            prev.Parameters.AddWithValue("d", fromDate);
            if (await prev.ExecuteScalarAsync(ct) is decimal p) previousClosing = p;
        }

        var totals = new List<DayTotals>();
        await using (var days = new NpgsqlCommand("""
            SELECT business_date,
                   COALESCE(SUM(amount) FILTER (WHERE transaction_type IN ('CREDIT','OPENING_BALANCE')), 0),
                   COALESCE(SUM(amount) FILTER (WHERE transaction_type = 'DEBIT'), 0)
              FROM daily_movements
             WHERE merchant_id = @m AND cash_account_id = @a AND currency = @c AND business_date >= @d
             GROUP BY business_date
            """, conn, tx))
        {
            days.Parameters.AddWithValue("m", merchantId);
            days.Parameters.AddWithValue("a", accountId);
            days.Parameters.AddWithValue("c", currency);
            days.Parameters.AddWithValue("d", fromDate);
            await using var r = await days.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                totals.Add(new DayTotals(r.GetFieldValue<DateOnly>(0), r.GetDecimal(1), r.GetDecimal(2)));
        }

        var balances = DailyBalanceCalculator.Recalculate(previousClosing, totals);
        if (balances.Count == 0) return 0;

        await using var upsert = new NpgsqlCommand("""
            INSERT INTO daily_balances AS b (merchant_id, cash_account_id, business_date, currency,
                                             opening_balance, total_credits, total_debits, closing_balance, updated_at)
            SELECT @m, @a, t.d, @c, t.o, t.cr, t.db, t.cl, now()
              FROM unnest(@dates, @opening, @credits, @debits, @closing) AS t(d, o, cr, db, cl)
            ON CONFLICT (merchant_id, cash_account_id, business_date, currency) DO UPDATE
               SET opening_balance = EXCLUDED.opening_balance,
                   total_credits   = EXCLUDED.total_credits,
                   total_debits    = EXCLUDED.total_debits,
                   closing_balance = EXCLUDED.closing_balance,
                   updated_at      = EXCLUDED.updated_at
            """, conn, tx);
        upsert.Parameters.AddWithValue("m", merchantId);
        upsert.Parameters.AddWithValue("a", accountId);
        upsert.Parameters.AddWithValue("c", currency);
        upsert.Parameters.Add(new NpgsqlParameter("dates", NpgsqlDbType.Array | NpgsqlDbType.Date) { Value = balances.Select(b => b.BusinessDate).ToArray() });
        upsert.Parameters.Add(new NpgsqlParameter("opening", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = balances.Select(b => b.OpeningBalance).ToArray() });
        upsert.Parameters.Add(new NpgsqlParameter("credits", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = balances.Select(b => b.TotalCredits).ToArray() });
        upsert.Parameters.Add(new NpgsqlParameter("debits", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = balances.Select(b => b.TotalDebits).ToArray() });
        upsert.Parameters.Add(new NpgsqlParameter("closing", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = balances.Select(b => b.ClosingBalance).ToArray() });
        await upsert.ExecuteNonQueryAsync(ct);
        return balances.Count;
    }

    private static async Task UpdateWatermarkAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Movement m, CancellationToken ct)
    {
        long contiguous = 0, maxSeen = 0;
        await using (var upsert = new NpgsqlCommand("""
            INSERT INTO account_watermarks (merchant_id, cash_account_id, currency)
            VALUES (@m, @a, @c)
            ON CONFLICT (merchant_id, cash_account_id) DO UPDATE SET currency = EXCLUDED.currency
            RETURNING contiguous_sequence, max_seen_sequence
            """, conn, tx))
        {
            upsert.Parameters.AddWithValue("m", m.MerchantId);
            upsert.Parameters.AddWithValue("a", m.CashAccountId);
            upsert.Parameters.AddWithValue("c", m.Currency);
            await using var r = await upsert.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { contiguous = r.GetInt64(0); maxSeen = r.GetInt64(1); }
        }

        if (m.AccountSequence is { } seq)
        {
            maxSeen = Math.Max(maxSeen, seq);
            var above = new List<long>();
            await using var q = new NpgsqlCommand("""
                SELECT account_sequence FROM daily_movements
                 WHERE merchant_id = @m AND cash_account_id = @a AND account_sequence > @c
                 ORDER BY account_sequence
                """, conn, tx);
            q.Parameters.AddWithValue("m", m.MerchantId);
            q.Parameters.AddWithValue("a", m.CashAccountId);
            q.Parameters.AddWithValue("c", contiguous);
            await using (var r = await q.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct)) above.Add(r.GetInt64(0));
            }
            contiguous = ConsistencyEvaluator.AdvanceContiguous(contiguous, above);
        }

        await ExecAsync(conn, tx, """
            UPDATE account_watermarks
               SET contiguous_sequence    = @contig,
                   max_seen_sequence      = @max,
                   last_event_occurred_on = GREATEST(COALESCE(last_event_occurred_on, @o), @o),
                   last_applied_at        = now(),
                   last_lag_seconds       = GREATEST(EXTRACT(EPOCH FROM now() - @o)::float8, 0)
             WHERE merchant_id = @m AND cash_account_id = @a
            """, ct, ("contig", contiguous), ("max", maxSeen), ("o", m.OccurredOn), ("m", m.MerchantId), ("a", m.CashAccountId));
    }

    public async Task<AccountState?> GetAccountStateAsync(Guid merchantId, Guid cashAccountId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT w.currency, w.contiguous_sequence, w.max_seen_sequence, w.last_event_occurred_on, w.last_applied_at, w.last_lag_seconds,
                   (SELECT max(h.last_seen_at) FROM consumer_heartbeats h WHERE h.consumer_name = @consumer)
              FROM account_watermarks w
             WHERE w.merchant_id = @m AND w.cash_account_id = @a
            """);
        cmd.Parameters.AddWithValue("m", merchantId);
        cmd.Parameters.AddWithValue("a", cashAccountId);
        cmd.Parameters.AddWithValue("consumer", CashTransactionCreatedHandler.ConsumerName);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        var watermark = new AccountWatermark(
            r.GetInt64(1), r.GetInt64(2),
            r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3),
            r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
            r.IsDBNull(5) ? null : r.GetDouble(5));
        var heartbeat = r.IsDBNull(6) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(6);
        return new AccountState(r.GetString(0), watermark, heartbeat);
    }

    public async Task<BalancesSnapshot> GetBalancesAsync(Guid merchantId, Guid cashAccountId, string currency, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT business_date, opening_balance, total_credits, total_debits, closing_balance, 0 AS kind
              FROM daily_balances
             WHERE merchant_id = @m AND cash_account_id = @a AND currency = @c AND business_date BETWEEN @from AND @to
            UNION ALL
            (SELECT business_date, opening_balance, total_credits, total_debits, closing_balance, 1 AS kind
               FROM daily_balances
              WHERE merchant_id = @m AND cash_account_id = @a AND currency = @c AND business_date < @from
              ORDER BY business_date DESC LIMIT 1)
            """);
        cmd.Parameters.AddWithValue("m", merchantId);
        cmd.Parameters.AddWithValue("a", cashAccountId);
        cmd.Parameters.AddWithValue("c", currency);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);

        decimal before = 0m;
        var list = new List<DailyBalance>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var b = new DailyBalance(r.GetFieldValue<DateOnly>(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4));
            if (r.GetInt32(5) == 1) before = b.ClosingBalance;
            else list.Add(b);
        }
        return new BalancesSnapshot(currency, before, list.OrderBy(b => b.BusinessDate).ToList());
    }

    public async Task<int> RebuildAllAsync(CancellationToken ct)
    {
        var accounts = new List<(Guid M, Guid A, string C)>();
        await using (var cmd = dataSource.CreateCommand("SELECT DISTINCT merchant_id, cash_account_id, currency FROM daily_movements"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) accounts.Add((r.GetGuid(0), r.GetGuid(1), r.GetString(2)));
        }

        foreach (var (m, a, c) in accounts)
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await LockAccountAsync(conn, tx, m, a, ct);
            await ExecAsync(conn, tx, "DELETE FROM daily_balances WHERE merchant_id = @m AND cash_account_id = @a AND currency = @c",
                ct, ("m", m), ("a", a), ("c", c));
            await RecalculateFromAsync(conn, tx, m, a, c, DateOnly.MinValue, ct);
            await tx.CommitAsync(ct);
        }
        return accounts.Count;
    }

    public async Task RecordHeartbeatAsync(string consumerName, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO consumer_heartbeats (consumer_name, instance_id, last_seen_at) VALUES (@c, @i, now())
            ON CONFLICT (consumer_name, instance_id) DO UPDATE SET last_seen_at = EXCLUDED.last_seen_at
            """);
        cmd.Parameters.AddWithValue("c", consumerName);
        cmd.Parameters.AddWithValue("i", InstanceId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Task LockAccountAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid merchantId, Guid accountId, CancellationToken ct) =>
        ExecAsync(conn, tx, "SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", ct, ("k", $"{merchantId}:{accountId}"));

    private static async Task<int> ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
