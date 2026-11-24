using DailyConsolidation.Domain;

namespace DailyConsolidation.Application;

/// <summary>Movimento validado, derivado de um evento de integração.</summary>
public sealed record Movement(
    Guid EventId,
    Guid MerchantId,
    Guid CashAccountId,
    Guid TransactionId,
    DateOnly BusinessDate,
    string Currency,
    string TransactionType,
    decimal Amount,
    Guid? ReversesTransactionId,
    long? AccountSequence,
    DateTimeOffset OccurredOn);

public enum ProjectionOutcome
{
    Applied,
    Duplicate
}

public sealed record ProjectionResult(ProjectionOutcome Outcome, int RecalculatedDays);

/// <summary>Estado conhecido de uma conta na projeção (null = conta desconhecida para este tenant).</summary>
public sealed record AccountState(string Currency, AccountWatermark Watermark, DateTimeOffset? ConsumerHeartbeat);

public sealed record BalancesSnapshot(string Currency, decimal ClosingBeforeFrom, IReadOnlyList<DailyBalance> Materialized);

public interface IProjectionStore
{
    /// <summary>
    /// Em uma única transação: advisory lock por conta, deduplicação por EventId (processed_events),
    /// registro do movimento, recálculo do dia afetado e posteriores, e atualização do watermark.
    /// </summary>
    Task<ProjectionResult> ApplyAsync(Movement movement, string consumerName, CancellationToken ct);

    Task<AccountState?> GetAccountStateAsync(Guid merchantId, Guid cashAccountId, CancellationToken ct);

    Task<BalancesSnapshot> GetBalancesAsync(Guid merchantId, Guid cashAccountId, string currency, DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Reconstrói a projeção inteira a partir de daily_movements (procedimento operacional — RF-DC-10).</summary>
    Task<int> RebuildAllAsync(CancellationToken ct);

    Task RecordHeartbeatAsync(string consumerName, CancellationToken ct);
}

/// <summary>Cache não autoritativo. Implementações nunca devem propagar falhas do Redis.</summary>
public interface IBalanceCache
{
    Task<BalancesSnapshot?> GetAsync(Guid merchantId, Guid cashAccountId, string currency, DateOnly from, DateOnly to, CancellationToken ct);
    Task SetAsync(Guid merchantId, Guid cashAccountId, DateOnly from, DateOnly to, BalancesSnapshot snapshot, CancellationToken ct);
    Task InvalidateAccountAsync(Guid merchantId, Guid cashAccountId, CancellationToken ct);
}

/// <summary>Evento inválido: falha permanente → não há retry, vai para a error queue para análise e reprocessamento.</summary>
public sealed class InvalidEventException(string message) : Exception(message);

public sealed class NotFoundException(string message) : Exception(message);

public sealed class ValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
