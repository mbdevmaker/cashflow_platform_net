namespace CashFlow.Domain;

public static class CashTransactionType
{
    public const string Credit = "CREDIT";
    public const string Debit = "DEBIT";
    public const string OpeningBalance = "OPENING_BALANCE";

    public static string ParseForRegistration(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToUpperInvariant();
        return v is Credit or Debit
            ? v
            : throw DomainException.Validation("type_invalid", "Tipo deve ser CREDIT ou DEBIT.");
    }

    /// <summary>Natureza inversa usada no estorno compensatório. Abertura é tratada como entrada (crédito).</summary>
    public static string Inverse(string type) => type switch
    {
        Credit or OpeningBalance => Debit,
        Debit => Credit,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };
}

public static class CashTransactionStatus
{
    public const string Confirmed = "CONFIRMED";
}

/// <summary>
/// Lançamento financeiro imutável. Correções somente via estorno compensatório (regra 6).
/// </summary>
public sealed class CashTransaction
{
    private CashTransaction()
    {
        Type = string.Empty; Currency = string.Empty; IdempotencyKey = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid CashAccountId { get; private set; }
    public string Type { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; }
    public string? Description { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public DateOnly BusinessDate { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public long AccountSequence { get; private set; }
    public string IdempotencyKey { get; private set; }
    public Guid? ReversesTransactionId { get; private set; }
    public string? CreatedBy { get; private set; }

    public string Status => CashTransactionStatus.Confirmed;
    public bool IsReversal => ReversesTransactionId.HasValue;

    public static CashTransaction Register(
        Guid id, CashAccount account, Guid merchantId, string? type, decimal amount, string? description,
        DateTimeOffset occurredAt, string idempotencyKey, string? createdBy, DateTimeOffset now)
    {
        return Create(id, account, merchantId, CashTransactionType.ParseForRegistration(type), amount, description,
            occurredAt, idempotencyKey, null, createdBy, now);
    }

    public static CashTransaction OpeningBalance(
        Guid id, CashAccount account, decimal amount, DateTimeOffset occurredAt, string? createdBy, DateTimeOffset now)
    {
        return Create(id, account, account.MerchantId, CashTransactionType.OpeningBalance, amount, "Saldo de abertura",
            occurredAt, $"opening-balance:{account.Id}", null, createdBy, now);
    }

    /// <summary>Cria o lançamento compensatório de natureza inversa, ligado ao original. O original não é alterado.</summary>
    public CashTransaction Reverse(
        Guid id, CashAccount account, string? reason, DateTimeOffset occurredAt, string idempotencyKey, string? createdBy, DateTimeOffset now)
    {
        if (IsReversal)
            throw DomainException.Rule("reversal_of_reversal", "Um estorno não pode ser estornado.");
        if (account.Id != CashAccountId)
            throw new InvalidOperationException("Conta informada não corresponde ao lançamento original.");

        var text = string.IsNullOrWhiteSpace(reason) ? $"Estorno de {Id}" : $"Estorno: {reason.Trim()}";
        if (text.Length > 500) text = text[..500];

        return Create(id, account, MerchantId, CashTransactionType.Inverse(Type), Amount, text,
            occurredAt, idempotencyKey, Id, createdBy, now);
    }

    private static CashTransaction Create(
        Guid id, CashAccount account, Guid merchantId, string type, decimal amount, string? description,
        DateTimeOffset occurredAt, string idempotencyKey, Guid? reverses, string? createdBy, DateTimeOffset now)
    {
        // Regra 2: nunca referenciar conta de outro comerciante.
        if (!account.BelongsTo(merchantId))
            throw DomainException.NotFound("account_not_found", "Conta de caixa não encontrada.");
        account.EnsureCanReceiveTransactions();
        Money.EnsureValidAmount(amount);

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            throw DomainException.Validation("idempotency_key_invalid", "Idempotency-Key obrigatório (até 200 caracteres).");
        if (description is { Length: > 500 })
            throw DomainException.Validation("description_too_long", "Descrição deve ter até 500 caracteres.");
        if (occurredAt == default)
            throw DomainException.Validation("occurred_at_required", "occurredAt é obrigatório.");
        if (occurredAt > now.AddMinutes(5))
            throw DomainException.Validation("occurred_at_future", "occurredAt não pode estar no futuro.");

        return new CashTransaction
        {
            Id = id,
            MerchantId = merchantId,
            CashAccountId = account.Id,
            Type = type,
            Amount = amount,
            Currency = account.Currency,
            Description = description?.Trim(),
            OccurredAt = occurredAt.ToUniversalTime(),
            BusinessDate = TimeZones.BusinessDateOf(occurredAt, account.TimeZone),
            CreatedAt = now,
            AccountSequence = account.NextSequence(),
            IdempotencyKey = idempotencyKey,
            ReversesTransactionId = reverses,
            CreatedBy = createdBy
        };
    }
}
