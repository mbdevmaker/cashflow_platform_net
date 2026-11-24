using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CashFlow.IntegrationTests;

/// <summary>Fluxos HTTP completos: API → Application → EF Core → PostgreSQL real.</summary>
[Collection(CashFlowCollection.Name)]
public sealed class TransactionApiTests(CashFlowFixture fx)
{
    private static readonly DateTimeOffset OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1);

    private static async Task<Guid> CreateAccountAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/cash-accounts",
            new { name = "Caixa de teste", currency = "BRL", timeZone = "America/Sao_Paulo" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PostTransactionAsync(HttpClient client, Guid accountId, string key, decimal amount = 100m, string type = "CREDIT")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions")
        {
            Content = JsonContent.Create(new { cashAccountId = accountId, type, amount, description = "teste", occurredAt = OccurredAt })
        };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostReversalAsync(HttpClient client, Guid transactionId, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/transactions/{transactionId}/reversals")
        {
            Content = JsonContent.Create(new { reason = "teste" })
        };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private static async Task<Guid> IdOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    private static async Task<string?> ProblemCodeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    [Fact]
    public async Task Requisicoes_paralelas_com_a_mesma_chave_criam_um_unico_lancamento()
    {
        var client = await fx.Api.ClientForAsync("A");
        var account = await CreateAccountAsync(client);
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => PostTransactionAsync(client, account, key)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        var ids = await Task.WhenAll(responses.Select(IdOf));
        Assert.Single(ids.Distinct());

        await using var conn = await fx.OpenConnectionAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM cash_transactions WHERE cash_account_id = @a AND idempotency_key = @k", conn);
        cmd.Parameters.AddWithValue("a", account);
        cmd.Parameters.AddWithValue("k", key);
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Mesma_chave_com_payload_diferente_retorna_409()
    {
        var client = await fx.Api.ClientForAsync("A");
        var account = await CreateAccountAsync(client);
        var key = Guid.NewGuid().ToString();

        Assert.Equal(HttpStatusCode.Created, (await PostTransactionAsync(client, account, key, 10m)).StatusCode);
        var conflict = await PostTransactionAsync(client, account, key, 99m);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency_key_reused", await ProblemCodeOf(conflict));
    }

    [Fact]
    public async Task Lancamento_sem_idempotency_key_retorna_400()
    {
        var client = await fx.Api.ClientForAsync("A");
        var account = await CreateAccountAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/transactions",
            new { cashAccountId = account, type = "CREDIT", amount = 1m, occurredAt = OccurredAt });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("idempotency_key_required", await ProblemCodeOf(response));
    }

    [Fact]
    public async Task Lancamentos_paralelos_na_mesma_conta_recebem_sequencia_sem_buracos()
    {
        var client = await fx.Api.ClientForAsync("A");
        var account = await CreateAccountAsync(client);
        const int total = 20;

        var responses = await Task.WhenAll(Enumerable.Range(0, total)
            .Select(_ => PostTransactionAsync(client, account, Guid.NewGuid().ToString(), 1m)));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));

        await using var conn = await fx.OpenConnectionAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT account_sequence FROM cash_transactions WHERE cash_account_id = @a ORDER BY account_sequence", conn);
        cmd.Parameters.AddWithValue("a", account);
        var sequences = new List<long>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync()) sequences.Add(reader.GetInt64(0));

        Assert.Equal(Enumerable.Range(1, total).Select(i => (long)i), sequences);
    }

    [Fact]
    public async Task Estornos_paralelos_do_mesmo_lancamento_geram_um_201_e_um_409()
    {
        var client = await fx.Api.ClientForAsync("A");
        var account = await CreateAccountAsync(client);
        var original = await IdOf(await PostTransactionAsync(client, account, Guid.NewGuid().ToString()));

        var responses = await Task.WhenAll(
            PostReversalAsync(client, original, Guid.NewGuid().ToString()),
            PostReversalAsync(client, original, Guid.NewGuid().ToString()));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var rejected = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("already_reversed", await ProblemCodeOf(rejected));
    }

    [Fact]
    public async Task Estorno_de_estorno_retorna_422()
    {
        var client = await fx.Api.ClientForAsync("A");
        var account = await CreateAccountAsync(client);
        var original = await IdOf(await PostTransactionAsync(client, account, Guid.NewGuid().ToString()));
        var reversal = await IdOf(await PostReversalAsync(client, original, Guid.NewGuid().ToString()));

        var response = await PostReversalAsync(client, reversal, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("reversal_of_reversal", await ProblemCodeOf(response));
    }

    [Fact]
    public async Task Comerciante_B_recebe_404_em_todos_os_recursos_do_comerciante_A()
    {
        var clientA = await fx.Api.ClientForAsync("A");
        var clientB = await fx.Api.ClientForAsync("B");
        var account = await CreateAccountAsync(clientA);
        var transaction = await IdOf(await PostTransactionAsync(clientA, account, Guid.NewGuid().ToString()));

        var attempts = new[]
        {
            await clientB.GetAsync($"/api/v1/cash-accounts/{account}"),
            await clientB.GetAsync($"/api/v1/transactions/{transaction}"),
            await clientB.GetAsync($"/api/v1/transactions?cashAccountId={account}"),
            await PostTransactionAsync(clientB, account, Guid.NewGuid().ToString()),
            await PostReversalAsync(clientB, transaction, Guid.NewGuid().ToString())
        };

        Assert.All(attempts, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
    }

    [Fact]
    public async Task Requisicao_sem_token_retorna_401()
    {
        var anonymous = fx.Api.CreateClient();
        var response = await anonymous.GetAsync($"/api/v1/transactions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
