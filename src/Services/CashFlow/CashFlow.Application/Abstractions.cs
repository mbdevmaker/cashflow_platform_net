using CashFlow.Domain;
using CashFlowPlatform.Contracts.Events;

namespace CashFlow.Application;

/// <summary>Registro de idempotência: (comerciante, operação, chave) → hash do payload e recurso criado.</summary>
public sealed class IdempotencyRecord
{
    private IdempotencyRecord() { Operation = string.Empty; IdempotencyKey = string.Empty; RequestHash = string.Empty; }

    public IdempotencyRecord(Guid merchantId, string operation, string key, string requestHash, Guid resourceId, int status, DateTimeOffset createdAt)
    {
        MerchantId = merchantId;
        Operation = operation;
        IdempotencyKey = key;
        RequestHash = requestHash;
        ResourceId = resourceId;
        ResponseStatus = status;
        CreatedAt = createdAt;
    }

    public Guid MerchantId { get; private set; }
    public string Operation { get; private set; }
    public string IdempotencyKey { get; private set; }
    public string RequestHash { get; private set; }
    public Guid ResourceId { get; private set; }
    public int ResponseStatus { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

public static class IdempotencyOperations
{
    public const string RegisterTransaction = "register-transaction";
    public const string ReverseTransaction = "reverse-transaction";
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalItems)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalItems / (double)PageSize);
}

public sealed record TransactionQuery(Guid MerchantId, Guid CashAccountId, DateOnly? From, DateOnly? To, int Page, int PageSize);

/// <summary>Porta de persistência do Cash Flow. Toda consulta exige o MerchantId do contexto autenticado.</summary>
public interface ICashFlowStore
{
    Task<Merchant?> FindMerchantAsync(Guid merchantId, CancellationToken ct);
    Task<IReadOnlyList<Merchant>> ListMerchantsAsync(CancellationToken ct);
    Task<CashAccount?> FindAccountAsync(Guid merchantId, Guid accountId, CancellationToken ct);

    /// <summary>Carrega a conta com lock de linha (SELECT ... FOR UPDATE) para alocar a sequência.</summary>
    Task<CashAccount?> LockAccountAsync(Guid merchantId, Guid accountId, CancellationToken ct);

    Task<IReadOnlyList<CashAccount>> ListAccountsAsync(Guid merchantId, CancellationToken ct);
    Task<CashTransaction?> FindTransactionAsync(Guid merchantId, Guid transactionId, CancellationToken ct);
    Task<CashTransaction?> FindReversalOfAsync(Guid merchantId, Guid originalTransactionId, CancellationToken ct);
    Task<PagedResult<CashTransaction>> ListTransactionsAsync(TransactionQuery query, CancellationToken ct);
    Task<IdempotencyRecord?> FindIdempotencyAsync(Guid merchantId, string operation, string key, CancellationToken ct);

    void Add(CashAccount account);
    void Add(CashTransaction transaction);
    void Add(IdempotencyRecord record);

    /// <summary>Grava o evento na Outbox; persistido no mesmo SaveChanges/commit do lançamento (RF-CF-09).</summary>
    void EnqueueOutbox(CashTransactionCreatedV1 integrationEvent);

    /// <summary>Executa o trabalho numa transação de banco; commit atômico de lançamento + Outbox + idempotência.</summary>
    Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
    void DiscardChanges();
}

/// <summary>Violação de unicidade traduzida pela infraestrutura (ex.: corrida na mesma Idempotency-Key).</summary>
public sealed class UniqueConstraintViolationException : Exception
{
    public UniqueConstraintViolationException(string constraintName, Exception inner)
        : base($"Violação de unicidade: {constraintName}", inner) => ConstraintName = constraintName;

    public string ConstraintName { get; }
}

public static class ConstraintNames
{
    public const string IdempotencyPk = "pk_idempotency_records";
    public const string SingleReversal = "uq_tx_single_reversal";
}
