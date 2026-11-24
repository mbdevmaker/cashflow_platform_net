using DailyConsolidation.Application;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Consolidation.IntegrationTests;

/// <summary>Projeção aplicada pelo handler real sobre PostgreSQL e Redis reais.</summary>
[Collection(ConsolidationCollection.Name)]
public sealed class ProjectionTests(ConsolidationFixture fx)
{
    private static readonly DateOnly Day10 = new(2026, 3, 10);
    private static readonly DateOnly Day11 = new(2026, 3, 11);
    private static readonly DateOnly Day12 = new(2026, 3, 12);

    private async Task<ProjectionResult> ApplyAsync(CashFlowPlatform.Contracts.Events.CashTransactionCreatedV1 evt)
    {
        await using var scope = fx.Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CashTransactionCreatedHandler>().HandleAsync(evt, CancellationToken.None);
    }

    private async Task<PeriodBalanceResponse> PeriodAsync(Guid merchant, Guid account, DateOnly from, DateOnly to)
    {
        await using var scope = fx.Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BalanceQueryService>().GetPeriodAsync(merchant, account, from, to, CancellationToken.None);
    }

    [Fact]
    public async Task Evento_duplicado_e_aplicado_uma_unica_vez()
    {
        var merchant = Guid.NewGuid();
        var account = Guid.NewGuid();
        var evt = ConsolidationFixture.Event(merchant, account, Day10, 100m);

        Assert.Equal(ProjectionOutcome.Applied, (await ApplyAsync(evt)).Outcome);
        Assert.Equal(ProjectionOutcome.Duplicate, (await ApplyAsync(evt)).Outcome);

        var period = await PeriodAsync(merchant, account, Day10, Day10);
        Assert.Equal(100m, period.ClosingBalance);
    }

    [Fact]
    public async Task Lancamento_retroativo_recalcula_os_dias_seguintes()
    {
        var merchant = Guid.NewGuid();
        var account = Guid.NewGuid();
        await ApplyAsync(ConsolidationFixture.Event(merchant, account, Day10, 100m, sequence: 1));
        await ApplyAsync(ConsolidationFixture.Event(merchant, account, Day12, 50m, sequence: 2));

        // Chega depois, mas pertence a um dia anterior ao último já consolidado.
        var result = await ApplyAsync(ConsolidationFixture.Event(merchant, account, Day11, 30m, "DEBIT", sequence: 3));

        Assert.Equal(2, result.RecalculatedDays);
        var days = (await PeriodAsync(merchant, account, Day10, Day12)).Days;
        Assert.Equal([100m, 70m, 120m], days.Select(d => d.ClosingBalance));
        Assert.Equal(70m, days[2].OpeningBalance);
    }

    [Fact]
    public async Task Ordem_de_chegada_nao_altera_o_resultado()
    {
        var merchant = Guid.NewGuid();
        var inOrder = Guid.NewGuid();
        var shuffled = Guid.NewGuid();
        var movements = Enumerable.Range(1, 12).Select(i => (
            Day: Day10.AddDays(i % 4),
            Amount: 10m * i,
            Type: i % 3 == 0 ? "DEBIT" : "CREDIT",
            Sequence: (long)i)).ToList();

        foreach (var m in movements)
            await ApplyAsync(ConsolidationFixture.Event(merchant, inOrder, m.Day, m.Amount, m.Type, m.Sequence));
        var random = new Random(42);
        var permutation = movements.OrderBy(_ => random.Next()).ToList();
        Assert.NotEqual(movements, permutation);
        foreach (var m in permutation)
            await ApplyAsync(ConsolidationFixture.Event(merchant, shuffled, m.Day, m.Amount, m.Type, m.Sequence));

        var expected = await PeriodAsync(merchant, inOrder, Day10, Day10.AddDays(3));
        var actual = await PeriodAsync(merchant, shuffled, Day10, Day10.AddDays(3));
        Assert.Equal(expected.Days, actual.Days);
    }

    [Fact]
    public async Task Eventos_paralelos_na_mesma_conta_somam_corretamente()
    {
        var merchant = Guid.NewGuid();
        var account = Guid.NewGuid();
        const int total = 20;

        await Task.WhenAll(Enumerable.Range(1, total)
            .Select(i => ApplyAsync(ConsolidationFixture.Event(merchant, account, Day10, 1m, sequence: i))));

        var period = await PeriodAsync(merchant, account, Day10, Day10);
        Assert.Equal(total, period.TotalCredits);

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT contiguous_sequence FROM account_watermarks WHERE cash_account_id = @a", conn);
        cmd.Parameters.AddWithValue("a", account);
        Assert.Equal((long)total, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Evento_de_conta_que_pertence_a_outro_comerciante_e_rejeitado()
    {
        var account = Guid.NewGuid();
        await ApplyAsync(ConsolidationFixture.Event(Guid.NewGuid(), account, Day10, 10m));

        await Assert.ThrowsAsync<InvalidEventException>(() =>
            ApplyAsync(ConsolidationFixture.Event(Guid.NewGuid(), account, Day10, 10m, sequence: 2)));
    }

    [Fact]
    public async Task Conta_nao_mistura_moedas()
    {
        var merchant = Guid.NewGuid();
        var account = Guid.NewGuid();
        await ApplyAsync(ConsolidationFixture.Event(merchant, account, Day10, 10m));

        await Assert.ThrowsAsync<InvalidEventException>(() =>
            ApplyAsync(ConsolidationFixture.Event(merchant, account, Day10, 10m, sequence: 2, currency: "USD")));
    }
}
