using CashFlow.Application;
using CashFlow.Domain;
using CashFlow.Infrastructure;
using CashFlowPlatform.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CashFlow.IntegrationTests;

/// <summary>Garantias que só existem no banco real: migrations, transação, FK composta, gatilho de imutabilidade.</summary>
[Collection(CashFlowCollection.Name)]
public sealed class PersistenceTests(CashFlowFixture fx)
{
    private static readonly Guid MerchantA = DevelopmentMerchants.A.Id;
    private static readonly Guid MerchantB = DevelopmentMerchants.B.Id;

    private async Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await fx.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<(CashAccount Account, CashTransaction Transaction)> PersistAccountWithTransactionAsync(Guid merchantId)
    {
        await using var scope = fx.Api.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ICashFlowStore>();
        var now = DateTimeOffset.UtcNow;
        var account = CashAccount.Open(Guid.CreateVersion7(), merchantId, "Conta", "BRL", "America/Sao_Paulo", now);
        var tx = CashTransaction.Register(Guid.CreateVersion7(), account, merchantId, "CREDIT", 10m, null, now.AddMinutes(-1), Guid.NewGuid().ToString(), "teste", now);
        await store.InTransactionAsync(async ct =>
        {
            store.Add(account);
            store.Add(tx);
            await store.SaveChangesAsync(ct);
            return true;
        }, CancellationToken.None);
        return (account, tx);
    }

    private static CashTransactionCreatedV1 EventFor(CashTransaction tx) => new()
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
        AccountSequence = tx.AccountSequence,
        CorrelationId = Guid.NewGuid()
    };

    [Fact]
    public async Task Migrations_criam_todas_as_tabelas_em_banco_vazio()
    {
        var tables = await CountAsync("""
            SELECT count(*) FROM information_schema.tables
             WHERE table_schema = 'public'
               AND table_name IN ('merchants','cash_accounts','cash_transactions','idempotency_records','outbox_messages')
            """);
        Assert.Equal(5L, tables);
    }

    [Fact]
    public async Task Falha_depois_do_insert_desfaz_lancamento_e_outbox_juntos()
    {
        await using var scope = fx.Api.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ICashFlowStore>();
        var now = DateTimeOffset.UtcNow;
        var account = CashAccount.Open(Guid.CreateVersion7(), MerchantA, "Atomicidade", "BRL", "America/Sao_Paulo", now);
        var tx = CashTransaction.Register(Guid.CreateVersion7(), account, MerchantA, "CREDIT", 10m, null, now.AddMinutes(-1), "atomic-key", "teste", now);
        var evt = EventFor(tx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InTransactionAsync<bool>(async ct =>
        {
            store.Add(account);
            store.Add(tx);
            store.EnqueueOutbox(evt);
            await store.SaveChangesAsync(ct);
            throw new InvalidOperationException("falha simulada depois do INSERT");
        }, CancellationToken.None));

        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM cash_accounts WHERE id = @id", ("id", account.Id)));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM cash_transactions WHERE id = @id", ("id", tx.Id)));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM outbox_messages WHERE id = @id", ("id", evt.EventId)));
    }

    [Fact]
    public async Task Banco_rejeita_lancamento_de_um_comerciante_na_conta_de_outro()
    {
        var (accountOfA, _) = await PersistAccountWithTransactionAsync(MerchantA);

        await using var conn = await fx.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO cash_transactions (id, merchant_id, cash_account_id, type, amount, currency, occurred_at,
                                           business_date, created_at, account_sequence, idempotency_key)
            VALUES (@id, @merchant, @account, 'CREDIT', 1, 'BRL', now(), current_date, now(), 999, 'cross-tenant')
            """, conn);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("merchant", MerchantB);
        cmd.Parameters.AddWithValue("account", accountOfA.Id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Theory]
    [InlineData("UPDATE cash_transactions SET amount = 999 WHERE id = @id")]
    [InlineData("DELETE FROM cash_transactions WHERE id = @id")]
    public async Task Lancamento_confirmado_nao_pode_ser_alterado_nem_apagado(string sql)
    {
        var (_, tx) = await PersistAccountWithTransactionAsync(MerchantA);

        await using var conn = await fx.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", tx.Id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Contains("imutável", ex.MessageText, StringComparison.Ordinal);
    }
}
