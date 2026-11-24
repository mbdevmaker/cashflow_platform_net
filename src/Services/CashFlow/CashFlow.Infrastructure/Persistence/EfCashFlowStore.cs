using System.Diagnostics;
using System.Text.Json;
using CashFlow.Application;
using CashFlow.Domain;
using CashFlowPlatform.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CashFlow.Infrastructure.Persistence;

internal sealed class EfCashFlowStore(CashFlowDbContext db, TimeProvider clock) : ICashFlowStore
{
    internal static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

    public Task<Merchant?> FindMerchantAsync(Guid merchantId, CancellationToken ct) =>
        db.Merchants.AsNoTracking().FirstOrDefaultAsync(m => m.Id == merchantId, ct);

    public async Task<IReadOnlyList<Merchant>> ListMerchantsAsync(CancellationToken ct) =>
        await db.Merchants.AsNoTracking().OrderBy(m => m.Name).ToListAsync(ct);

    public Task<CashAccount?> FindAccountAsync(Guid merchantId, Guid accountId, CancellationToken ct) =>
        db.CashAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId && a.MerchantId == merchantId, ct);

    public async Task<CashAccount?> LockAccountAsync(Guid merchantId, Guid accountId, CancellationToken ct)
    {
        // Sem composição LINQ sobre o FromSql para manter o FOR UPDATE no nível superior da consulta.
        var rows = await db.CashAccounts
            .FromSql($"SELECT * FROM cash_accounts WHERE id = {accountId} AND merchant_id = {merchantId} FOR UPDATE")
            .AsTracking()
            .ToListAsync(ct);
        return rows.FirstOrDefault();
    }

    public async Task<IReadOnlyList<CashAccount>> ListAccountsAsync(Guid merchantId, CancellationToken ct) =>
        await db.CashAccounts.AsNoTracking().Where(a => a.MerchantId == merchantId).OrderBy(a => a.CreatedAt).ToListAsync(ct);

    public Task<CashTransaction?> FindTransactionAsync(Guid merchantId, Guid transactionId, CancellationToken ct) =>
        db.CashTransactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == transactionId && t.MerchantId == merchantId, ct);

    public Task<CashTransaction?> FindReversalOfAsync(Guid merchantId, Guid originalTransactionId, CancellationToken ct) =>
        db.CashTransactions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.MerchantId == merchantId && t.ReversesTransactionId == originalTransactionId, ct);

    public async Task<PagedResult<CashTransaction>> ListTransactionsAsync(TransactionQuery q, CancellationToken ct)
    {
        var query = db.CashTransactions.AsNoTracking()
            .Where(t => t.MerchantId == q.MerchantId && t.CashAccountId == q.CashAccountId);
        if (q.From is { } from) query = query.Where(t => t.BusinessDate >= from);
        if (q.To is { } to) query = query.Where(t => t.BusinessDate <= to);

        var total = await query.LongCountAsync(ct);
        var items = await query
            .OrderBy(t => t.OccurredAt).ThenBy(t => t.Id) // ordenação estável
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .ToListAsync(ct);
        return new PagedResult<CashTransaction>(items, q.Page, q.PageSize, total);
    }

    public Task<IdempotencyRecord?> FindIdempotencyAsync(Guid merchantId, string operation, string key, CancellationToken ct) =>
        db.IdempotencyRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.MerchantId == merchantId && r.Operation == operation && r.IdempotencyKey == key, ct);

    public void Add(CashAccount account) => db.CashAccounts.Add(account);
    public void Add(CashTransaction transaction) => db.CashTransactions.Add(transaction);
    public void Add(IdempotencyRecord record) => db.IdempotencyRecords.Add(record);

    public void EnqueueOutbox(CashTransactionCreatedV1 integrationEvent)
    {
        var now = clock.GetUtcNow();
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = integrationEvent.EventId, // eventId = PK da Outbox = MessageId AMQP
            EventType = CashTransactionCreatedV1.EventTypeName,
            SchemaVersion = CashTransactionCreatedV1.Version,
            Payload = JsonSerializer.Serialize(integrationEvent, EventJson),
            TraceParent = Activity.Current?.Id, // W3C traceparent para trace ponta a ponta
            CreatedAt = now,
            NextAttemptAt = now
        });
    }

    public async Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
            return await work(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var result = await work(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw new UniqueConstraintViolationException(pg.ConstraintName ?? "unknown", e);
        }
    }

    public void DiscardChanges() => db.ChangeTracker.Clear();
}
