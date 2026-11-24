using CashFlow.Application;
using CashFlow.Domain;
using CashFlowPlatform.Contracts.Events;

namespace CashFlow.UnitTests;

public class CashFlowServiceTests
{
    private static readonly Guid MerchantA = Guid.NewGuid();
    private static readonly Guid MerchantB = Guid.NewGuid();

    private readonly InMemoryStore _store = new();
    private readonly CashFlowService _svc;
    private readonly CashAccount _accountA;

    public CashFlowServiceTests()
    {
        _svc = new CashFlowService(_store, TimeProvider.System);
        _store.Merchants.Add(new Merchant(MerchantA, "A", "America/Sao_Paulo", DateTimeOffset.UtcNow));
        _store.Merchants.Add(new Merchant(MerchantB, "B", "America/Sao_Paulo", DateTimeOffset.UtcNow));
        _accountA = CashAccount.Open(Guid.NewGuid(), MerchantA, "Caixa", "BRL", "America/Sao_Paulo", DateTimeOffset.UtcNow);
        _store.Accounts.Add(_accountA);
    }

    private RegisterTransactionCommand Cmd(decimal amount = 100m) =>
        new(_accountA.Id, "CREDIT", amount, "Venda", DateTimeOffset.UtcNow.AddMinutes(-1));

    [Fact]
    public async Task Registro_grava_lancamento_e_evento_na_outbox()
    {
        var result = await _svc.RegisterTransactionAsync(MerchantA, "u", "k1", Cmd(), Guid.NewGuid(), default);

        Assert.False(result.Replayed);
        Assert.Single(_store.Transactions);
        var evt = Assert.Single(_store.Outbox);
        Assert.Equal(result.Value.Id, evt.TransactionId);
        Assert.Equal(MerchantA, evt.MerchantId);
        Assert.Equal(1, evt.AccountSequence);
        Assert.Equal(CashTransactionCreatedV1.Version, evt.SchemaVersion);
    }

    [Fact]
    public async Task Mesma_chave_e_mesmo_payload_retorna_resultado_original_sem_duplicar()
    {
        var cmd = Cmd();
        var first = await _svc.RegisterTransactionAsync(MerchantA, "u", "k1", cmd, Guid.NewGuid(), default);
        var second = await _svc.RegisterTransactionAsync(MerchantA, "u", "k1", cmd, Guid.NewGuid(), default);

        Assert.True(second.Replayed);
        Assert.Equal(first.Value.Id, second.Value.Id);
        Assert.Single(_store.Transactions);
        Assert.Single(_store.Outbox);
    }

    [Fact]
    public async Task Mesma_chave_com_payload_diferente_retorna_conflito()
    {
        await _svc.RegisterTransactionAsync(MerchantA, "u", "k1", Cmd(100m), Guid.NewGuid(), default);
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _svc.RegisterTransactionAsync(MerchantA, "u", "k1", Cmd(200m), Guid.NewGuid(), default));
        Assert.Equal(DomainErrorKind.Conflict, ex.Kind);
    }

    [Fact]
    public async Task Sem_idempotency_key_e_rejeitado()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _svc.RegisterTransactionAsync(MerchantA, "u", null, Cmd(), Guid.NewGuid(), default));
        Assert.Equal(DomainErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Comerciante_B_nao_registra_na_conta_de_A()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _svc.RegisterTransactionAsync(MerchantB, "u", "k1", Cmd(), Guid.NewGuid(), default));
        Assert.Equal(DomainErrorKind.NotFound, ex.Kind);
        Assert.Empty(_store.Transactions);
    }

    [Fact]
    public async Task Comerciante_B_nao_le_nem_estorna_lancamento_de_A()
    {
        var created = await _svc.RegisterTransactionAsync(MerchantA, "u", "k1", Cmd(), Guid.NewGuid(), default);

        var read = await Assert.ThrowsAsync<DomainException>(() => _svc.GetTransactionAsync(MerchantB, created.Value.Id, default));
        Assert.Equal(DomainErrorKind.NotFound, read.Kind);

        var list = await Assert.ThrowsAsync<DomainException>(() => _svc.ListTransactionsAsync(MerchantB, _accountA.Id, null, null, 1, 50, default));
        Assert.Equal(DomainErrorKind.NotFound, list.Kind);

        var rev = await Assert.ThrowsAsync<DomainException>(() =>
            _svc.ReverseTransactionAsync(MerchantB, "u", "r1", created.Value.Id, new ReverseTransactionCommand("x", null), Guid.NewGuid(), default));
        Assert.Equal(DomainErrorKind.NotFound, rev.Kind);
    }

    [Fact]
    public async Task Estorno_duplicado_e_rejeitado_e_original_preservado()
    {
        var created = await _svc.RegisterTransactionAsync(MerchantA, "u", "k1", Cmd(), Guid.NewGuid(), default);
        var reversal = await _svc.ReverseTransactionAsync(MerchantA, "u", "r1", created.Value.Id, new ReverseTransactionCommand("erro", null), Guid.NewGuid(), default);

        Assert.Equal(CashTransactionType.Debit, reversal.Value.Type);
        Assert.Equal(created.Value.Id, reversal.Value.ReversesTransactionId);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _svc.ReverseTransactionAsync(MerchantA, "u", "r2", created.Value.Id, new ReverseTransactionCommand("de novo", null), Guid.NewGuid(), default));
        Assert.Equal(DomainErrorKind.Conflict, ex.Kind);
        Assert.Equal(2, _store.Transactions.Count);
    }

    [Fact]
    public async Task PageSize_acima_do_limite_e_rejeitado()
    {
        await Assert.ThrowsAsync<DomainException>(() => _svc.ListTransactionsAsync(MerchantA, _accountA.Id, null, null, 1, 101, default));
    }

    [Fact]
    public async Task Conta_com_saldo_de_abertura_gera_lancamento_e_evento_de_abertura()
    {
        var account = await _svc.CreateAccountAsync(MerchantA, "u",
            new CreateCashAccountCommand("Filial", "BRL", "America/Sao_Paulo", 1000m, null), Guid.NewGuid(), default);

        var opening = Assert.Single(_store.Transactions, t => t.CashAccountId == account.Id);
        Assert.Equal(CashTransactionType.OpeningBalance, opening.Type);
        Assert.Contains(_store.Outbox, e => e.TransactionType == "OPENING_BALANCE" && e.Amount == 1000m);
    }

    /// <summary>Store em memória que respeita o filtro por MerchantId (simula o contrato da porta).</summary>
    private sealed class InMemoryStore : ICashFlowStore
    {
        public List<Merchant> Merchants { get; } = [];
        public List<CashAccount> Accounts { get; } = [];
        public List<CashTransaction> Transactions { get; } = [];
        public List<IdempotencyRecord> Idempotency { get; } = [];
        public List<CashTransactionCreatedV1> Outbox { get; } = [];

        private readonly List<object> _pending = [];

        public Task<Merchant?> FindMerchantAsync(Guid merchantId, CancellationToken ct) => Task.FromResult(Merchants.FirstOrDefault(m => m.Id == merchantId));
        public Task<IReadOnlyList<Merchant>> ListMerchantsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Merchant>>(Merchants);
        public Task<CashAccount?> FindAccountAsync(Guid merchantId, Guid accountId, CancellationToken ct) =>
            Task.FromResult(Accounts.FirstOrDefault(a => a.Id == accountId && a.MerchantId == merchantId));
        public Task<CashAccount?> LockAccountAsync(Guid merchantId, Guid accountId, CancellationToken ct) => FindAccountAsync(merchantId, accountId, ct);
        public Task<IReadOnlyList<CashAccount>> ListAccountsAsync(Guid merchantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CashAccount>>(Accounts.Where(a => a.MerchantId == merchantId).ToList());
        public Task<CashTransaction?> FindTransactionAsync(Guid merchantId, Guid transactionId, CancellationToken ct) =>
            Task.FromResult(Transactions.FirstOrDefault(t => t.Id == transactionId && t.MerchantId == merchantId));
        public Task<CashTransaction?> FindReversalOfAsync(Guid merchantId, Guid originalTransactionId, CancellationToken ct) =>
            Task.FromResult(Transactions.FirstOrDefault(t => t.MerchantId == merchantId && t.ReversesTransactionId == originalTransactionId));
        public Task<PagedResult<CashTransaction>> ListTransactionsAsync(TransactionQuery q, CancellationToken ct)
        {
            var items = Transactions.Where(t => t.MerchantId == q.MerchantId && t.CashAccountId == q.CashAccountId).ToList();
            return Task.FromResult(new PagedResult<CashTransaction>(items, q.Page, q.PageSize, items.Count));
        }
        public Task<IdempotencyRecord?> FindIdempotencyAsync(Guid merchantId, string operation, string key, CancellationToken ct) =>
            Task.FromResult(Idempotency.FirstOrDefault(r => r.MerchantId == merchantId && r.Operation == operation && r.IdempotencyKey == key));

        public void Add(CashAccount account) => _pending.Add(account);
        public void Add(CashTransaction transaction) => _pending.Add(transaction);
        public void Add(IdempotencyRecord record) => _pending.Add(record);
        public void EnqueueOutbox(CashTransactionCreatedV1 integrationEvent) => _pending.Add(integrationEvent);

        public Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct) => work(ct);

        public Task SaveChangesAsync(CancellationToken ct)
        {
            foreach (var item in _pending)
            {
                switch (item)
                {
                    case CashAccount a: Accounts.Add(a); break;
                    case CashTransaction t: Transactions.Add(t); break;
                    case IdempotencyRecord r: Idempotency.Add(r); break;
                    case CashTransactionCreatedV1 e: Outbox.Add(e); break;
                }
            }
            _pending.Clear();
            return Task.CompletedTask;
        }

        public void DiscardChanges() => _pending.Clear();
    }
}
