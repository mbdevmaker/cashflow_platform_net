namespace CashFlowPlatform.Contracts.Events;

/// <summary>
/// Contrato público de integração (docs/integration/contratos-eventos.md).
/// Usado para lançamentos e estornos: estornos chegam com tipo invertido e <see cref="ReversesTransactionId"/> preenchido.
/// Evolução compatível: somente campos opcionais adicionados. Mudança incompatível = novo tipo V2.
/// </summary>
public sealed record CashTransactionCreatedV1
{
    public const string EventTypeName = "CashTransactionCreated";
    public const int Version = 1;

    /// <summary>Nome explícito da exchange no RabbitMQ (desacoplado do namespace .NET).</summary>
    public const string ExchangeName = "cashflow.cash-transaction-created.v1";

    public required Guid EventId { get; init; }
    public string EventType { get; init; } = EventTypeName;
    public int SchemaVersion { get; init; } = Version;
    public required DateTimeOffset OccurredOn { get; init; }
    public required Guid MerchantId { get; init; }
    public required Guid CashAccountId { get; init; }
    public required Guid TransactionId { get; init; }

    /// <summary>CREDIT | DEBIT | OPENING_BALANCE.</summary>
    public required string TransactionType { get; init; }

    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public required DateOnly BusinessDate { get; init; }
    public Guid? ReversesTransactionId { get; init; }

    /// <summary>Campo aditivo (ADR-013): sequência monotônica sem lacunas por conta.</summary>
    public long? AccountSequence { get; init; }

    public required Guid CorrelationId { get; init; }
}

public static class TransactionTypes
{
    public const string Credit = "CREDIT";
    public const string Debit = "DEBIT";
    public const string OpeningBalance = "OPENING_BALANCE";

    public static bool IsValid(string? value) => value is Credit or Debit or OpeningBalance;
}
