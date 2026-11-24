using CashFlow.Domain;
using CashFlowPlatform.Contracts.Events;

namespace CashFlow.Application;

/// <summary>Casos de uso do Cash Flow (escrita e leitura). O MerchantId sempre vem do contexto autenticado.</summary>
public sealed class CashFlowService(ICashFlowStore store, TimeProvider clock)
{
    public const int MaxPageSize = 100;
    public const int MaxPeriodDays = 366;

    public async Task<CashAccountResponse> CreateAccountAsync(Guid merchantId, string? userId, CreateCashAccountCommand cmd, Guid correlationId, CancellationToken ct)
    {
        var merchant = await store.FindMerchantAsync(merchantId, ct);
        if (merchant is null || !merchant.IsActive)
            throw DomainException.Rule("merchant_inactive", "Comerciante inexistente ou inativo.");

        var now = clock.GetUtcNow();
        var account = CashAccount.Open(Guid.CreateVersion7(), merchantId, cmd.Name, cmd.Currency, cmd.TimeZone ?? merchant.TimeZone, now);

        CashTransaction? opening = null;
        if (cmd.OpeningBalance is { } openingAmount)
        {
            // Regra 12: saldo inicial representado por lançamento de abertura auditável.
            opening = CashTransaction.OpeningBalance(Guid.CreateVersion7(), account, openingAmount, cmd.OpeningBalanceAt ?? now, userId, now);
        }

        await store.InTransactionAsync(async token =>
        {
            store.Add(account);
            if (opening is not null)
            {
                store.Add(opening);
                store.EnqueueOutbox(ToEvent(opening, correlationId));
            }
            await store.SaveChangesAsync(token);
            return true;
        }, ct);

        return CashAccountResponse.From(account);
    }

    public async Task<IReadOnlyList<CashAccountResponse>> ListAccountsAsync(Guid merchantId, CancellationToken ct) =>
        (await store.ListAccountsAsync(merchantId, ct)).Select(CashAccountResponse.From).ToList();

    public async Task<CashAccountResponse> GetAccountAsync(Guid merchantId, Guid accountId, CancellationToken ct)
    {
        var account = await store.FindAccountAsync(merchantId, accountId, ct)
                      ?? throw DomainException.NotFound("account_not_found", "Conta de caixa não encontrada.");
        return CashAccountResponse.From(account);
    }

    public async Task<IdempotentResult<TransactionResponse>> RegisterTransactionAsync(
        Guid merchantId, string? userId, string? idempotencyKey, RegisterTransactionCommand cmd, Guid correlationId, CancellationToken ct)
    {
        var key = RequireKey(idempotencyKey);
        var hash = RequestHasher.Hash(cmd.CashAccountId, cmd.Type?.Trim().ToUpperInvariant(), cmd.Amount, cmd.Description, cmd.OccurredAt);
        const string op = IdempotencyOperations.RegisterTransaction;

        var replay = await TryReplayAsync(merchantId, op, key, hash, ct);
        if (replay is not null) return replay;

        try
        {
            return await store.InTransactionAsync(async token =>
            {
                var account = await store.LockAccountAsync(merchantId, cmd.CashAccountId, token)
                              ?? throw DomainException.NotFound("account_not_found", "Conta de caixa não encontrada.");

                // Rechecagem sob o lock da conta: requisições concorrentes na mesma conta são serializadas.
                var inner = await TryReplayAsync(merchantId, op, key, hash, token);
                if (inner is not null) return inner;

                var now = clock.GetUtcNow();
                var tx = CashTransaction.Register(Guid.CreateVersion7(), account, merchantId, cmd.Type, cmd.Amount,
                    cmd.Description, cmd.OccurredAt, key, userId, now);

                store.Add(tx);
                store.Add(new IdempotencyRecord(merchantId, op, key, hash, tx.Id, 201, now));
                store.EnqueueOutbox(ToEvent(tx, correlationId));
                await store.SaveChangesAsync(token);

                return new IdempotentResult<TransactionResponse>(TransactionResponse.From(tx), false);
            }, ct);
        }
        catch (UniqueConstraintViolationException e) when (e.ConstraintName == ConstraintNames.IdempotencyPk)
        {
            // Corrida entre contas diferentes com a mesma chave: o vencedor já gravou; responder conforme o registro.
            store.DiscardChanges();
            var winner = await TryReplayAsync(merchantId, op, key, hash, ct);
            if (winner is null) throw;
            return winner;
        }
    }

    public async Task<IdempotentResult<TransactionResponse>> ReverseTransactionAsync(
        Guid merchantId, string? userId, string? idempotencyKey, Guid transactionId, ReverseTransactionCommand cmd, Guid correlationId, CancellationToken ct)
    {
        var key = RequireKey(idempotencyKey);
        var hash = RequestHasher.Hash(transactionId, cmd.Reason, cmd.OccurredAt);
        const string op = IdempotencyOperations.ReverseTransaction;

        var replay = await TryReplayAsync(merchantId, op, key, hash, ct);
        if (replay is not null) return replay;

        try
        {
            return await store.InTransactionAsync(async token =>
            {
                var original = await store.FindTransactionAsync(merchantId, transactionId, token)
                               ?? throw DomainException.NotFound("transaction_not_found", "Lançamento não encontrado.");

                var account = await store.LockAccountAsync(merchantId, original.CashAccountId, token)
                              ?? throw DomainException.NotFound("transaction_not_found", "Lançamento não encontrado.");

                var inner = await TryReplayAsync(merchantId, op, key, hash, token);
                if (inner is not null) return inner;

                if (await store.FindReversalOfAsync(merchantId, original.Id, token) is not null)
                    throw DomainException.Conflict("already_reversed", "Este lançamento já foi estornado.");

                var now = clock.GetUtcNow();
                var reversal = original.Reverse(Guid.CreateVersion7(), account, cmd.Reason, cmd.OccurredAt ?? now, key, userId, now);

                store.Add(reversal);
                store.Add(new IdempotencyRecord(merchantId, op, key, hash, reversal.Id, 201, now));
                store.EnqueueOutbox(ToEvent(reversal, correlationId));
                await store.SaveChangesAsync(token);

                return new IdempotentResult<TransactionResponse>(TransactionResponse.From(reversal), false);
            }, ct);
        }
        catch (UniqueConstraintViolationException e) when (e.ConstraintName == ConstraintNames.SingleReversal)
        {
            store.DiscardChanges();
            throw DomainException.Conflict("already_reversed", "Este lançamento já foi estornado.");
        }
        catch (UniqueConstraintViolationException e) when (e.ConstraintName == ConstraintNames.IdempotencyPk)
        {
            store.DiscardChanges();
            var winner = await TryReplayAsync(merchantId, op, key, hash, ct);
            if (winner is null) throw;
            return winner;
        }
    }

    public async Task<TransactionResponse> GetTransactionAsync(Guid merchantId, Guid transactionId, CancellationToken ct)
    {
        var tx = await store.FindTransactionAsync(merchantId, transactionId, ct)
                 ?? throw DomainException.NotFound("transaction_not_found", "Lançamento não encontrado.");
        return TransactionResponse.From(tx);
    }

    public async Task<PagedResult<TransactionResponse>> ListTransactionsAsync(
        Guid merchantId, Guid cashAccountId, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1) throw DomainException.Validation("page_invalid", "page deve ser >= 1.");
        if (pageSize is < 1 or > MaxPageSize) throw DomainException.Validation("page_size_invalid", $"pageSize deve estar entre 1 e {MaxPageSize}.");
        if (from is { } f && to is { } t && (t < f || t.DayNumber - f.DayNumber > MaxPeriodDays))
            throw DomainException.Validation("period_invalid", $"Período inválido (to >= from, no máximo {MaxPeriodDays} dias).");

        // Conta de outro tenant → 404, sem revelar existência.
        _ = await store.FindAccountAsync(merchantId, cashAccountId, ct)
            ?? throw DomainException.NotFound("account_not_found", "Conta de caixa não encontrada.");

        var result = await store.ListTransactionsAsync(new TransactionQuery(merchantId, cashAccountId, from, to, page, pageSize), ct);
        return new PagedResult<TransactionResponse>(result.Items.Select(TransactionResponse.From).ToList(), result.Page, result.PageSize, result.TotalItems);
    }

    private async Task<IdempotentResult<TransactionResponse>?> TryReplayAsync(Guid merchantId, string op, string key, string hash, CancellationToken ct)
    {
        var record = await store.FindIdempotencyAsync(merchantId, op, key, ct);
        if (record is null) return null;

        if (!string.Equals(record.RequestHash, hash, StringComparison.Ordinal))
            throw DomainException.Conflict("idempotency_key_reused", "Idempotency-Key já utilizada com payload diferente.");

        var tx = await store.FindTransactionAsync(merchantId, record.ResourceId, ct)
                 ?? throw new InvalidOperationException("Registro de idempotência aponta para lançamento inexistente.");
        return new IdempotentResult<TransactionResponse>(TransactionResponse.From(tx), true);
    }

    private static string RequireKey(string? key)
    {
        var k = key?.Trim();
        if (string.IsNullOrEmpty(k) || k.Length > 200)
            throw DomainException.Validation("idempotency_key_required", "Header Idempotency-Key é obrigatório (até 200 caracteres).");
        return k;
    }

    internal static CashTransactionCreatedV1 ToEvent(CashTransaction tx, Guid correlationId) => new()
    {
        EventId = Guid.CreateVersion7(),
        OccurredOn = tx.CreatedAt,
        MerchantId = tx.MerchantId,
        CashAccountId = tx.CashAccountId,
        TransactionId = tx.Id,
        TransactionType = tx.Type,
        Amount = tx.Amount,
        Currency = tx.Currency,
        BusinessDate = tx.BusinessDate,
        ReversesTransactionId = tx.ReversesTransactionId,
        AccountSequence = tx.AccountSequence,
        CorrelationId = correlationId
    };
}
