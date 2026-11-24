using CashFlowPlatform.Contracts.Events;
using DailyConsolidation.Application;
using DailyConsolidation.Domain;

namespace Consolidation.UnitTests;

public class CalculatorTests
{
    private static readonly DateOnly D1 = new(2026, 11, 1);

    [Fact]
    public void Soma_creditos_e_debitos_e_propaga_saldo_final_como_abertura()
    {
        var result = DailyBalanceCalculator.Recalculate(1000m,
        [
            new DayTotals(D1, 500m, 200m),
            new DayTotals(D1.AddDays(1), 0m, 50m)
        ]);

        Assert.Equal(new DailyBalance(D1, 1000m, 500m, 200m, 1300m), result[0]);
        Assert.Equal(new DailyBalance(D1.AddDays(1), 1300m, 0m, 50m, 1250m), result[1]);
    }

    [Fact]
    public void Resultado_independe_da_ordem_de_chegada_dos_dias()
    {
        DayTotals[] days = [new(D1.AddDays(2), 10m, 0m), new(D1, 5m, 1m), new(D1.AddDays(1), 0m, 3m)];
        var a = DailyBalanceCalculator.Recalculate(0m, days);
        var b = DailyBalanceCalculator.Recalculate(0m, days.Reverse());
        Assert.Equal(a, b);
        Assert.Equal(11m, a[^1].ClosingBalance);
    }

    [Fact]
    public void Dia_sem_movimentacao_tem_abertura_igual_fechamento_do_dia_anterior()
    {
        var materialized = DailyBalanceCalculator.Recalculate(0m, [new DayTotals(D1, 100m, 0m), new DayTotals(D1.AddDays(3), 0m, 40m)]);
        var period = DailyBalanceCalculator.FillPeriod(D1, D1.AddDays(4), 0m, materialized);

        Assert.Equal(5, period.Count);
        Assert.Equal(DailyBalance.Empty(D1.AddDays(1), 100m), period[1]);
        Assert.Equal(DailyBalance.Empty(D1.AddDays(2), 100m), period[2]);
        Assert.Equal(60m, period[3].ClosingBalance);
        Assert.Equal(DailyBalance.Empty(D1.AddDays(4), 60m), period[4]);
    }

    [Fact]
    public void Periodo_antes_de_qualquer_movimento_usa_saldo_anterior()
    {
        var period = DailyBalanceCalculator.FillPeriod(D1.AddDays(10), D1.AddDays(11), 250m, []);
        Assert.All(period, d => Assert.Equal(250m, d.ClosingBalance));
    }

    [Fact]
    public void Saldo_negativo_e_apresentado_sem_truncamento()
    {
        var result = DailyBalanceCalculator.Recalculate(0m, [new DayTotals(D1, 0m, 123.4567m)]);
        Assert.Equal(-123.4567m, result[0].ClosingBalance);
    }
}

public class ConsistencyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly TimeSpan Slo = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Hb = TimeSpan.FromSeconds(20);

    [Fact]
    public void Sem_lacunas_e_consumidor_vivo_e_CURRENT()
    {
        var info = ConsistencyEvaluator.Evaluate(new AccountWatermark(5, 5, Now, Now, 0.2), Now, Now, Slo, Hb);
        Assert.Equal(ConsistencyStatus.Current, info.Status);
    }

    [Fact]
    public void Lacuna_recente_e_PROCESSING_e_antiga_e_DELAYED()
    {
        Assert.Equal(ConsistencyStatus.Processing,
            ConsistencyEvaluator.Evaluate(new AccountWatermark(3, 5, Now, Now.AddSeconds(-1), 0.1), Now, Now, Slo, Hb).Status);
        Assert.Equal(ConsistencyStatus.Delayed,
            ConsistencyEvaluator.Evaluate(new AccountWatermark(3, 5, Now, Now.AddSeconds(-30), 0.1), Now, Now, Slo, Hb).Status);
    }

    [Fact]
    public void Consumidor_sem_heartbeat_e_UNKNOWN()
    {
        Assert.Equal(ConsistencyStatus.Unknown,
            ConsistencyEvaluator.Evaluate(new AccountWatermark(5, 5, Now, Now, 0), Now.AddMinutes(-2), Now, Slo, Hb).Status);
        Assert.Equal(ConsistencyStatus.Unknown, ConsistencyEvaluator.Evaluate(null, Now, Now, Slo, Hb).Status);
    }

    [Theory]
    [InlineData(0, new long[] { 1, 2, 3 }, 3)]
    [InlineData(0, new long[] { 2, 3 }, 0)]      // evento 1 ainda não chegou (fora de ordem)
    [InlineData(1, new long[] { 3, 2, 5 }, 3)]  // chegada fora de ordem; lacuna em 4
    [InlineData(2, new long[] { 3, 3, 4 }, 4)]  // duplicatas não quebram o cálculo
    public void Watermark_avanca_somente_sobre_sequencia_contigua(long start, long[] applied, long expected)
    {
        Assert.Equal(expected, ConsistencyEvaluator.AdvanceContiguous(start, applied));
    }
}

public class HandlerTests
{
    private static CashTransactionCreatedV1 Event(string type = "CREDIT", decimal amount = 10m,
        int version = 1, string currency = "BRL", Guid? merchant = null) => new()
    {
        EventId = Guid.NewGuid(),
        OccurredOn = DateTimeOffset.UtcNow,
        MerchantId = merchant ?? Guid.NewGuid(),
        CashAccountId = Guid.NewGuid(),
        TransactionId = Guid.NewGuid(),
        TransactionType = type,
        Amount = amount,
        Currency = currency,
        BusinessDate = new DateOnly(2026, 11, 17),
        SchemaVersion = version,
        AccountSequence = 1,
        CorrelationId = Guid.NewGuid()
    };

    [Fact]
    public async Task Evento_duplicado_nao_reaplica_efeito_nem_invalida_cache()
    {
        var store = new FakeStore();
        var cache = new FakeCache();
        var handler = new CashTransactionCreatedHandler(store, cache, TimeProvider.System);
        var evt = Event();

        var first = await handler.HandleAsync(evt, default);
        var second = await handler.HandleAsync(evt, default);

        Assert.Equal(ProjectionOutcome.Applied, first.Outcome);
        Assert.Equal(ProjectionOutcome.Duplicate, second.Outcome);
        Assert.Single(store.Applied);
        Assert.Equal(1, cache.Invalidations);
    }

    [Theory]
    [InlineData("TRANSFER", 10, 1, "BRL")]
    [InlineData("CREDIT", 0, 1, "BRL")]
    [InlineData("CREDIT", -5, 1, "BRL")]
    [InlineData("CREDIT", 10, 2, "BRL")]
    [InlineData("CREDIT", 10, 1, "real")]
    public async Task Evento_invalido_e_falha_permanente(string type, decimal amount, int version, string currency)
    {
        var handler = new CashTransactionCreatedHandler(new FakeStore(), new FakeCache(), TimeProvider.System);
        await Assert.ThrowsAsync<InvalidEventException>(() => handler.HandleAsync(Event(type: type, amount: amount, version: version, currency: currency), default));
    }

    [Fact]
    public async Task Evento_sem_merchant_e_rejeitado()
    {
        var handler = new CashTransactionCreatedHandler(new FakeStore(), new FakeCache(), TimeProvider.System);
        await Assert.ThrowsAsync<InvalidEventException>(() => handler.HandleAsync(Event(merchant: Guid.Empty), default));
    }

    private sealed class FakeStore : IProjectionStore
    {
        private readonly HashSet<Guid> _processed = [];
        public List<Movement> Applied { get; } = [];

        public Task<ProjectionResult> ApplyAsync(Movement movement, string consumerName, CancellationToken ct)
        {
            if (!_processed.Add(movement.EventId)) return Task.FromResult(new ProjectionResult(ProjectionOutcome.Duplicate, 0));
            Applied.Add(movement);
            return Task.FromResult(new ProjectionResult(ProjectionOutcome.Applied, 1));
        }

        public Task<AccountState?> GetAccountStateAsync(Guid merchantId, Guid cashAccountId, CancellationToken ct) => Task.FromResult<AccountState?>(null);
        public Task<BalancesSnapshot> GetBalancesAsync(Guid merchantId, Guid cashAccountId, string currency, DateOnly from, DateOnly to, CancellationToken ct) =>
            Task.FromResult(new BalancesSnapshot(currency, 0m, []));
        public Task<int> RebuildAllAsync(CancellationToken ct) => Task.FromResult(0);
        public Task RecordHeartbeatAsync(string consumerName, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeCache : IBalanceCache
    {
        public int Invalidations { get; private set; }
        public Task<BalancesSnapshot?> GetAsync(Guid merchantId, Guid cashAccountId, string currency, DateOnly from, DateOnly to, CancellationToken ct) =>
            Task.FromResult<BalancesSnapshot?>(null);
        public Task SetAsync(Guid merchantId, Guid cashAccountId, DateOnly from, DateOnly to, BalancesSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;
        public Task InvalidateAccountAsync(Guid merchantId, Guid cashAccountId, CancellationToken ct)
        {
            Invalidations++;
            return Task.CompletedTask;
        }
    }
}
