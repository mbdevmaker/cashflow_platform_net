using System.Diagnostics.Metrics;
using CashFlowPlatform.Contracts.Events;

namespace DailyConsolidation.Application;

/// <summary>Processa CashTransactionCreated v1 de forma idempotente (RF-DC-01/02/03/04).</summary>
public sealed class CashTransactionCreatedHandler(IProjectionStore store, IBalanceCache cache, TimeProvider clock)
{
    public const string ConsumerName = "daily-consolidation";
    public const string MeterName = "DailyConsolidation";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Applied = Meter.CreateCounter<long>("consolidation.events_applied");
    private static readonly Counter<long> Duplicates = Meter.CreateCounter<long>("consolidation.duplicates_detected");
    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>("consolidation.events_rejected");
    private static readonly Histogram<double> Lag = Meter.CreateHistogram<double>("consolidation.lag_seconds", "s",
        "Tempo entre o registro no Cash Flow e a aplicação na projeção");
    private static readonly Histogram<int> Recalculated = Meter.CreateHistogram<int>("consolidation.recalculated_days");

    public async Task<ProjectionResult> HandleAsync(CashTransactionCreatedV1 evt, CancellationToken ct)
    {
        Movement movement;
        try
        {
            movement = EventValidator.ToMovement(evt);
        }
        catch (InvalidEventException)
        {
            Rejected.Add(1);
            throw;
        }

        var result = await store.ApplyAsync(movement, ConsumerName, ct);

        if (result.Outcome == ProjectionOutcome.Duplicate)
        {
            Duplicates.Add(1);
            return result;
        }

        Applied.Add(1);
        Recalculated.Record(result.RecalculatedDays);
        Lag.Record(Math.Max(0, (clock.GetUtcNow() - movement.OccurredOn).TotalSeconds));

        // Após o commit: invalidação best-effort (cache versionado; a implementação não propaga falhas).
        await cache.InvalidateAccountAsync(movement.MerchantId, movement.CashAccountId, ct);
        return result;
    }
}

public static class EventValidator
{
    public static Movement ToMovement(CashTransactionCreatedV1? evt)
    {
        if (evt is null) throw new InvalidEventException("Evento vazio.");
        if (evt.SchemaVersion != CashTransactionCreatedV1.Version)
            throw new InvalidEventException($"schemaVersion {evt.SchemaVersion} não suportada.");
        if (!string.Equals(evt.EventType, CashTransactionCreatedV1.EventTypeName, StringComparison.Ordinal))
            throw new InvalidEventException($"eventType '{evt.EventType}' inesperado.");
        if (evt.EventId == Guid.Empty || evt.MerchantId == Guid.Empty || evt.CashAccountId == Guid.Empty || evt.TransactionId == Guid.Empty)
            throw new InvalidEventException("Identificadores obrigatórios ausentes (eventId, merchantId, cashAccountId, transactionId).");
        if (!TransactionTypes.IsValid(evt.TransactionType))
            throw new InvalidEventException($"transactionType '{evt.TransactionType}' inválido.");
        if (evt.Amount <= 0 || decimal.Round(evt.Amount, 4) != evt.Amount)
            throw new InvalidEventException("amount deve ser positivo com até 4 casas decimais.");
        if (evt.Currency is not { Length: 3 } || !evt.Currency.All(char.IsAsciiLetterUpper))
            throw new InvalidEventException("currency deve ser ISO 4217.");
        if (evt.BusinessDate == default)
            throw new InvalidEventException("businessDate obrigatório.");
        if (evt.AccountSequence is <= 0)
            throw new InvalidEventException("accountSequence deve ser positivo quando informado.");

        return new Movement(evt.EventId, evt.MerchantId, evt.CashAccountId, evt.TransactionId, evt.BusinessDate,
            evt.Currency, evt.TransactionType, evt.Amount, evt.ReversesTransactionId, evt.AccountSequence, evt.OccurredOn.ToUniversalTime());
    }
}
