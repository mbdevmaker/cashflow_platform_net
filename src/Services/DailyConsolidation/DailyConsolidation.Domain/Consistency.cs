namespace DailyConsolidation.Domain;

public static class ConsistencyStatus
{
    public const string Current = "CURRENT";
    public const string Processing = "PROCESSING";
    public const string Delayed = "DELAYED";
    public const string Unknown = "UNKNOWN";
}

/// <summary>Posição de processamento conhecida de uma conta (ADR-013).</summary>
public sealed record AccountWatermark(
    long ContiguousSequence,
    long MaxSeenSequence,
    DateTimeOffset? LastEventOccurredOn,
    DateTimeOffset? LastAppliedAt,
    double? LastLagSeconds);

public sealed record ConsistencyInfo(string Status, DateTimeOffset? LastUpdatedAt, double? LagSeconds, long? LastAppliedSequence);

public static class ConsistencyEvaluator
{
    /// <summary>
    /// Estado baseado em watermark/posição (não apenas em UpdatedAt):
    /// UNKNOWN: sem watermark, sem sequência ou consumidor sem heartbeat recente;
    /// PROCESSING: lacuna conhecida (max_seen &gt; contiguous) dentro do SLO;
    /// DELAYED: lacuna persistindo além do SLO;
    /// CURRENT: sem lacunas conhecidas.
    /// </summary>
    public static ConsistencyInfo Evaluate(
        AccountWatermark? watermark, DateTimeOffset? consumerHeartbeat, DateTimeOffset now, TimeSpan slo, TimeSpan heartbeatTimeout)
    {
        if (watermark is null)
            return new ConsistencyInfo(ConsistencyStatus.Unknown, null, null, null);

        var info = new ConsistencyInfo(ConsistencyStatus.Current, watermark.LastAppliedAt, watermark.LastLagSeconds, watermark.ContiguousSequence);

        if (consumerHeartbeat is null || now - consumerHeartbeat.Value > heartbeatTimeout || watermark.MaxSeenSequence == 0)
            return info with { Status = ConsistencyStatus.Unknown };

        if (watermark.MaxSeenSequence > watermark.ContiguousSequence)
        {
            var since = watermark.LastAppliedAt ?? now;
            return info with { Status = now - since > slo ? ConsistencyStatus.Delayed : ConsistencyStatus.Processing };
        }

        return info;
    }

    /// <summary>Avança a sequência contígua dado o conjunto de sequências já aplicadas acima dela.</summary>
    public static long AdvanceContiguous(long contiguous, IEnumerable<long> appliedAbove)
    {
        foreach (var seq in appliedAbove.Where(s => s > contiguous).Distinct().Order())
        {
            if (seq != contiguous + 1) break;
            contiguous = seq;
        }
        return contiguous;
    }
}
