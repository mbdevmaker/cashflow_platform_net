using DailyConsolidation.Infrastructure;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Consolidation.IntegrationTests;

/// <summary>RabbitMQ real → consumer MassTransit → projeção; eventos inválidos param na fila _error.</summary>
[Collection(ConsolidationCollection.Name)]
public sealed class ConsumerTests(ConsolidationFixture fx)
{
    private static readonly DateOnly Day = new(2026, 5, 20);

    [Fact]
    public async Task Evento_publicado_no_broker_e_consolidado_e_invalido_vai_para_a_fila_de_erro()
    {
        await using var consumer = fx.CreateConsumer();
        var bus = consumer.Services.GetRequiredService<IBusControl>();
        // Saudável = endpoint de recepção pronto, com a fila já ligada ao exchange; antes disso a mensagem se perderia.
        Assert.Equal(BusHealthStatus.Healthy, await bus.WaitForHealthStatus(BusHealthStatus.Healthy, TimeSpan.FromSeconds(45)));

        // Evento válido: chega à projeção.
        var merchant = Guid.NewGuid();
        var account = Guid.NewGuid();
        await bus.Publish(ConsolidationFixture.Event(merchant, account, Day, 75m));

        var closing = await WaitForAsync(async () =>
        {
            await using var conn = new NpgsqlConnection(fx.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT closing_balance FROM daily_balances WHERE cash_account_id = @a AND business_date = @d", conn);
            cmd.Parameters.AddWithValue("a", account);
            cmd.Parameters.AddWithValue("d", Day);
            return await cmd.ExecuteScalarAsync() is decimal value ? value : (decimal?)null;
        });
        Assert.Equal(75m, closing);

        // Evento inválido (tipo desconhecido): falha permanente, sem retry, direto para _error.
        var errorQueue = DependencyInjection.QueueName + "_error";
        var before = await MessageCountAsync(errorQueue) ?? 0;
        await bus.Publish(ConsolidationFixture.Event(merchant, Guid.NewGuid(), Day, 10m, type: "TRANSFER"));

        var after = await WaitForAsync(async () => await MessageCountAsync(errorQueue) is { } n && n > before ? n : (uint?)null);
        Assert.Equal(before + 1, after);
    }

    /// <summary>Quantidade de mensagens na fila, ou null se ela ainda não existe.</summary>
    private async Task<uint?> MessageCountAsync(string queue)
    {
        var factory = new ConnectionFactory { Uri = new Uri(fx.RabbitConnectionString) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        try
        {
            return (await channel.QueueDeclarePassiveAsync(queue)).MessageCount;
        }
        catch (OperationInterruptedException)
        {
            return null;
        }
    }

    private static async Task<T?> WaitForAsync<T>(Func<Task<T?>> probe) where T : struct
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } value) return value;
            await Task.Delay(250);
        }
        return null;
    }
}
