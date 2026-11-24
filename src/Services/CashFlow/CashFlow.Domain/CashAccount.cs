namespace CashFlow.Domain;

public sealed class CashAccount
{
    private CashAccount() { Name = string.Empty; Currency = string.Empty; TimeZone = string.Empty; Status = string.Empty; }

    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public string Name { get; private set; }
    public string Currency { get; private set; }
    public string TimeZone { get; private set; }
    public string Status { get; private set; }

    /// <summary>Última sequência atribuída a um lançamento desta conta (watermark — ADR-013).</summary>
    public long LastSequence { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static CashAccount Open(Guid id, Guid merchantId, string? name, string? currency, string? timeZone, DateTimeOffset now)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > 150)
            throw DomainException.Validation("name_invalid", "Nome da conta é obrigatório e deve ter até 150 caracteres.");

        var tz = TimeZones.Resolve(timeZone);

        return new CashAccount
        {
            Id = id,
            MerchantId = merchantId,
            Name = trimmed,
            Currency = Money.NormalizeCurrency(currency),
            TimeZone = tz.Id,
            Status = CashAccountStatus.Active,
            LastSequence = 0,
            CreatedAt = now
        };
    }

    public bool BelongsTo(Guid merchantId) => MerchantId == merchantId;

    public void EnsureCanReceiveTransactions()
    {
        if (Status != CashAccountStatus.Active)
            throw DomainException.Rule("account_inactive", "Conta de caixa não está ativa.");
    }

    public long NextSequence() => ++LastSequence;
}

public static class CashAccountStatus
{
    public const string Active = "ACTIVE";
    public const string Closed = "CLOSED";
}
