using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DailyConsolidation.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Consolidation.IntegrationTests;

/// <summary>Consultas HTTP de saldo: autenticação, isolamento e degradação sem Redis.</summary>
[Collection(ConsolidationCollection.Name)]
public sealed class BalanceApiTests(ConsolidationFixture fx)
{
    private static readonly DateOnly Day = new(2026, 4, 15);

    private async Task<(Guid Merchant, Guid Account)> SeedAccountAsync(decimal credit, decimal debit)
    {
        var merchant = Guid.NewGuid();
        var account = Guid.NewGuid();
        await using var scope = fx.Api.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<CashTransactionCreatedHandler>();
        await handler.HandleAsync(ConsolidationFixture.Event(merchant, account, Day, credit, sequence: 1), CancellationToken.None);
        await handler.HandleAsync(ConsolidationFixture.Event(merchant, account, Day, debit, "DEBIT", sequence: 2), CancellationToken.None);
        return (merchant, account);
    }

    [Fact]
    public async Task Consulta_por_periodo_inclui_dias_sem_movimento()
    {
        var (merchant, account) = await SeedAccountAsync(500m, 200m);
        var client = fx.Api.ClientFor(merchant);

        var response = await client.GetAsync($"/api/v1/daily-balances?cashAccountId={account}&from={Day.AddDays(-1):yyyy-MM-dd}&to={Day.AddDays(1):yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var days = body.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(3, days.Count);
        Assert.Equal([0m, 300m, 300m], days.Select(d => d.GetProperty("closingBalance").GetDecimal()));
        Assert.Equal(300m, days[2].GetProperty("openingBalance").GetDecimal());
    }

    [Fact]
    public async Task Outro_comerciante_recebe_404()
    {
        var (_, account) = await SeedAccountAsync(10m, 1m);
        var intruder = fx.Api.ClientFor(Guid.NewGuid());

        var responses = new[]
        {
            await intruder.GetAsync($"/api/v1/daily-balances/{Day:yyyy-MM-dd}?cashAccountId={account}"),
            await intruder.GetAsync($"/api/v1/daily-balances?cashAccountId={account}&from={Day:yyyy-MM-dd}&to={Day:yyyy-MM-dd}"),
            await intruder.GetAsync($"/api/v1/daily-balances/status?cashAccountId={account}")
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
    }

    [Fact]
    public async Task Periodo_acima_de_366_dias_retorna_400()
    {
        var (merchant, account) = await SeedAccountAsync(10m, 1m);
        var client = fx.Api.ClientFor(merchant);

        var response = await client.GetAsync($"/api/v1/daily-balances?cashAccountId={account}&from=2025-01-01&to=2026-12-31");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Sem_token_retorna_401()
    {
        var response = await fx.Api.CreateClient().GetAsync($"/api/v1/daily-balances/status?cashAccountId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Consulta_continua_respondendo_com_o_redis_fora_do_ar()
    {
        var (merchant, account) = await SeedAccountAsync(80m, 30m);
        await using var withoutRedis = fx.CreateApiWithoutRedis();
        var client = withoutRedis.ClientFor(merchant);

        var response = await client.GetAsync($"/api/v1/daily-balances/{Day:yyyy-MM-dd}?cashAccountId={account}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(50m, body.GetProperty("closingBalance").GetDecimal());
    }
}
