using CashFlow.Domain;

namespace CashFlow.Application;

public sealed record CreateCashAccountCommand(string? Name, string? Currency, string? TimeZone, decimal? OpeningBalance, DateTimeOffset? OpeningBalanceAt);

public sealed record CashAccountResponse(Guid Id, string Name, string Currency, string TimeZone, string Status, DateTimeOffset CreatedAt)
{
    public static CashAccountResponse From(CashAccount a) => new(a.Id, a.Name, a.Currency, a.TimeZone, a.Status, a.CreatedAt);
}

public sealed record RegisterTransactionCommand(Guid CashAccountId, string? Type, decimal Amount, string? Description, DateTimeOffset OccurredAt);

public sealed record ReverseTransactionCommand(string? Reason, DateTimeOffset? OccurredAt);

public sealed record TransactionResponse(
    Guid Id,
    Guid CashAccountId,
    string Type,
    decimal Amount,
    string Currency,
    string? Description,
    DateTimeOffset OccurredAt,
    DateOnly BusinessDate,
    string Status,
    DateTimeOffset CreatedAt,
    Guid? ReversesTransactionId)
{
    public static TransactionResponse From(CashTransaction t) => new(
        t.Id, t.CashAccountId, t.Type, t.Amount, t.Currency, t.Description, t.OccurredAt, t.BusinessDate,
        t.Status, t.CreatedAt, t.ReversesTransactionId);
}

/// <summary>Resultado de comando idempotente: <see cref="Replayed"/> = true quando a mesma chave+payload já havia sido processada.</summary>
public sealed record IdempotentResult<T>(T Value, bool Replayed);
