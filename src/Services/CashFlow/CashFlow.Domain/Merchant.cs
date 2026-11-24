namespace CashFlow.Domain;

public sealed class Merchant
{
    private Merchant() { Name = string.Empty; TimeZone = string.Empty; Status = string.Empty; }

    public Merchant(Guid id, string name, string timeZone, DateTimeOffset createdAt)
    {
        TimeZones.Resolve(timeZone);
        Id = id;
        Name = name;
        TimeZone = timeZone;
        Status = MerchantStatus.Active;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public string TimeZone { get; private set; }
    public string Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsActive => Status == MerchantStatus.Active;
}

public static class MerchantStatus
{
    public const string Active = "ACTIVE";
    public const string Suspended = "SUSPENDED";
}
