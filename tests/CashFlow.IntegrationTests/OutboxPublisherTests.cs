using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CashFlowPlatform.Contracts.Events;
using Npgsql;
using RabbitMQ.Client;

namespace CashFlow.IntegrationTests;

/// <summary>Outbox → RabbitMQ real: a mensagem chega ao exchange e só então recebe published_at.</summary>
[Collection(CashFlowCollection.Name)]
public sealed class OutboxPublisherTests(CashFlowFixture fx)
{
    [Fact]
    public async Task Lancamento_e_publicado_no_exchange_e_marcado_como_publicado()
    {
        // Observador: fila própria ligada ao exchange do evento (mesmos argumentos que o MassTransit declara).
        var factory = new ConnectionFactory { Uri = new Uri(fx.RabbitConnectionString) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(CashTransactionCreatedV1.ExchangeName, ExchangeType.Fanout, durable: true, autoDelete: false);
        var queue = await channel.QueueDeclareAsync($"it-observer-{Guid.NewGuid():N}", durable: false, exclusive: true, autoDelete: true);
        await channel.QueueBindAsync(queue.QueueName, CashTransactionCreatedV1.ExchangeName, routingKey: string.Empty);

        // Lançamento pela API (grava a Outbox; a API não publica).
        var client = await fx.Api.ClientForAsync("A");
        var account = await client.PostAsJsonAsync("/api/v1/cash-accounts", new { name = "Outbox", currency = "BRL", timeZone = "America/Sao_Paulo" });
        var accountId = (await account.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions")
        {
            Content = JsonContent.Create(new { cashAccountId = accountId, type = "CREDIT", amount = 42.5m, occurredAt = DateTimeOffset.UtcNow.AddMinutes(-1) })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var created = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var transactionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Sobe o worker outbox-publisher apontando para o RabbitMQ do container.
        await using var publisher = fx.CreatePublisher();
        _ = publisher.Services;

        var publishedAt = await WaitForAsync(async () =>
        {
            await using var conn = await fx.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT published_at FROM outbox_messages WHERE payload->>'transactionId' = @t", conn);
            cmd.Parameters.AddWithValue("t", transactionId.ToString());
            return await cmd.ExecuteScalarAsync() is DateTime value ? value : (DateTime?)null;
        });
        Assert.True(publishedAt.HasValue, await DescribeOutboxRowAsync(transactionId));

        var delivered = await WaitForAsync(async () =>
        {
            var message = await channel.BasicGetAsync(queue.QueueName, autoAck: true);
            if (message is null) return (bool?)null;
            return Encoding.UTF8.GetString(message.Body.Span).Contains(transactionId.ToString(), StringComparison.OrdinalIgnoreCase) ? true : null;
        });
        Assert.True(delivered);
    }

    private async Task<string> DescribeOutboxRowAsync(Guid transactionId)
    {
        await using var conn = await fx.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT 'retry_count=' || retry_count || ' last_error=' || coalesce(last_error, '(nenhum)')
              FROM outbox_messages WHERE payload->>'transactionId' = @t
            """, conn);
        cmd.Parameters.AddWithValue("t", transactionId.ToString());
        return $"Mensagem da Outbox não publicada: {await cmd.ExecuteScalarAsync() ?? "linha não encontrada"}";
    }

    private static async Task<T?> WaitForAsync<T>(Func<Task<T?>> probe) where T : struct
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } value) return value;
            await Task.Delay(200);
        }
        return null;
    }
}
