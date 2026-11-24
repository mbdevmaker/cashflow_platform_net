using DailyConsolidation.Domain;

namespace DailyConsolidation.Application;

public sealed record ConsistencyDto(string Status, DateTimeOffset? LastUpdatedAt, double? LagSeconds);

public sealed record DayBalanceDto(DateOnly BusinessDate, decimal OpeningBalance, decimal TotalCredits, decimal TotalDebits, decimal ClosingBalance);

public sealed record PeriodBalanceResponse(
    Guid CashAccountId, string Currency, DateOnly From, DateOnly To,
    decimal OpeningBalance, decimal TotalCredits, decimal TotalDebits, decimal ClosingBalance,
    ConsistencyDto Consistency, IReadOnlyList<DayBalanceDto> Days);

public sealed record DayBalanceResponse(
    Guid CashAccountId, string Currency, DateOnly BusinessDate,
    decimal OpeningBalance, decimal TotalCredits, decimal TotalDebits, decimal ClosingBalance,
    ConsistencyDto Consistency);

public sealed record ProjectionStatusResponse(Guid CashAccountId, ConsistencyDto Consistency, long? LastAppliedSequence, DateTimeOffset? LastEventOccurredOn);

public sealed class ConsistencyOptions
{
    public int SloSeconds { get; set; } = 5;
    public int HeartbeatTimeoutSeconds { get; set; } = 20;
}

/// <summary>Consultas de saldo (cache-aside no Redis com fallback ao PostgreSQL). Tenant sempre do token.</summary>
public sealed class BalanceQueryService(IProjectionStore store, IBalanceCache cache, TimeProvider clock, ConsistencyOptions options)
{
    public const int MaxPeriodDays = 366;

    public async Task<PeriodBalanceResponse> GetPeriodAsync(Guid merchantId, Guid cashAccountId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from || to.DayNumber - from.DayNumber + 1 > MaxPeriodDays)
            throw new ValidationException("period_invalid", $"Período inválido (to >= from, no máximo {MaxPeriodDays} dias).");

        var (state, consistency) = await LoadStateAsync(merchantId, cashAccountId, ct);
        var snapshot = await LoadBalancesAsync(merchantId, cashAccountId, state.Currency, from, to, ct);
        var days = DailyBalanceCalculator.FillPeriod(from, to, snapshot.ClosingBeforeFrom, snapshot.Materialized);

        return new PeriodBalanceResponse(
            cashAccountId, state.Currency, from, to,
            OpeningBalance: days[0].OpeningBalance,
            TotalCredits: days.Sum(d => d.TotalCredits),
            TotalDebits: days.Sum(d => d.TotalDebits),
            ClosingBalance: days[^1].ClosingBalance,
            consistency,
            days.Select(ToDto).ToList());
    }

    public async Task<DayBalanceResponse> GetDayAsync(Guid merchantId, Guid cashAccountId, DateOnly date, CancellationToken ct)
    {
        var (state, consistency) = await LoadStateAsync(merchantId, cashAccountId, ct);
        var snapshot = await LoadBalancesAsync(merchantId, cashAccountId, state.Currency, date, date, ct);
        var day = DailyBalanceCalculator.FillPeriod(date, date, snapshot.ClosingBeforeFrom, snapshot.Materialized)[0];
        return new DayBalanceResponse(cashAccountId, state.Currency, date, day.OpeningBalance, day.TotalCredits,
            day.TotalDebits, day.ClosingBalance, consistency);
    }

    public async Task<ProjectionStatusResponse> GetStatusAsync(Guid merchantId, Guid cashAccountId, CancellationToken ct)
    {
        var (state, consistency) = await LoadStateAsync(merchantId, cashAccountId, ct);
        return new ProjectionStatusResponse(cashAccountId, consistency, state.Watermark.ContiguousSequence, state.Watermark.LastEventOccurredOn);
    }

    private async Task<(AccountState State, ConsistencyDto Consistency)> LoadStateAsync(Guid merchantId, Guid cashAccountId, CancellationToken ct)
    {
        // Conta inexistente OU de outro comerciante → 404 sem distinção (não vaza existência).
        var state = await store.GetAccountStateAsync(merchantId, cashAccountId, ct)
                    ?? throw new NotFoundException("Conta não encontrada na consolidação.");

        var info = ConsistencyEvaluator.Evaluate(state.Watermark, state.ConsumerHeartbeat, clock.GetUtcNow(),
            TimeSpan.FromSeconds(options.SloSeconds), TimeSpan.FromSeconds(options.HeartbeatTimeoutSeconds));
        var lag = info.LagSeconds is { } l ? Math.Round(l, 3) : (double?)null;
        return (state, new ConsistencyDto(info.Status, info.LastUpdatedAt, lag));
    }

    private async Task<BalancesSnapshot> LoadBalancesAsync(Guid merchantId, Guid cashAccountId, string currency, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var cached = await cache.GetAsync(merchantId, cashAccountId, currency, from, to, ct);
        if (cached is not null) return cached;

        var snapshot = await store.GetBalancesAsync(merchantId, cashAccountId, currency, from, to, ct);
        await cache.SetAsync(merchantId, cashAccountId, from, to, snapshot, ct);
        return snapshot;
    }

    private static DayBalanceDto ToDto(DailyBalance b) =>
        new(b.BusinessDate, b.OpeningBalance, b.TotalCredits, b.TotalDebits, b.ClosingBalance);
}
