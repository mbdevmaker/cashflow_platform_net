namespace CashFlow.Domain;

public static class TimeZones
{
    /// <summary>Valida que o identificador é IANA (ex.: America/Sao_Paulo) e existe no sistema.</summary>
    public static TimeZoneInfo Resolve(string? ianaId)
    {
        var id = (ianaId ?? string.Empty).Trim();
        if (id.Length == 0 || !id.Contains('/', StringComparison.Ordinal) && !string.Equals(id, "UTC", StringComparison.Ordinal))
            throw DomainException.Validation("timezone_invalid", "Fuso horário deve ser um identificador IANA (ex.: America/Sao_Paulo).");

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz))
            throw DomainException.Validation("timezone_invalid", $"Fuso horário '{id}' desconhecido.");
        return tz;
    }

    /// <summary>Data operacional = data local de OccurredAt no fuso da conta (regra 10).</summary>
    public static DateOnly BusinessDateOf(DateTimeOffset occurredAt, string ianaId)
    {
        var tz = Resolve(ianaId);
        var local = TimeZoneInfo.ConvertTime(occurredAt, tz);
        return DateOnly.FromDateTime(local.DateTime);
    }
}
