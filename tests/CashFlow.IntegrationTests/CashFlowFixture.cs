using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CashFlow.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace CashFlow.IntegrationTests;

/// <summary>
/// Sobe PostgreSQL e RabbitMQ reais (Testcontainers) uma única vez para toda a coleção,
/// aplica as migrations versionadas e semeia os comerciantes de desenvolvimento A e B.
/// Requer Docker em execução.
/// </summary>
public sealed class CashFlowFixture : IAsyncLifetime
{
    public const string SigningKey = "integration-tests-signing-key-0123456789abcdef";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.12-alpine")
        .WithDatabase("cashflow")
        .Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.3.8-management-alpine")
        .WithUsername("guest")
        .WithPassword("guest")
        .Build();

    public CashFlowApiFactory Api { get; private set; } = default!;
    public string ConnectionString => _postgres.GetConnectionString();
    public string RabbitConnectionString => _rabbit.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        Api = new CashFlowApiFactory(ConnectionString, rabbit: null);
        await DependencyInjection.MigrateAndSeedAsync(Api.Services, seedDevelopmentData: true);
    }

    public async Task DisposeAsync()
    {
        await Api.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }

    /// <summary>Instância no papel outbox-publisher apontando para o RabbitMQ do container.</summary>
    public CashFlowApiFactory CreatePublisher()
    {
        var uri = new Uri(_rabbit.GetConnectionString());
        return new CashFlowApiFactory(ConnectionString, rabbit: (uri.Host, uri.Port));
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}

[CollectionDefinition(Name)]
public sealed class CashFlowCollection : ICollectionFixture<CashFlowFixture>
{
    public const string Name = "cashflow-integration";
}

public sealed class CashFlowApiFactory(string connectionString, (string Host, int Port)? rabbit) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:CashFlow", connectionString);
        builder.UseSetting("Authentication:Mode", "Development");
        builder.UseSetting("Authentication:DevSigningKey", CashFlowFixture.SigningKey);
        builder.UseSetting("RateLimiting:PermitsPerSecond", "100000");

        if (rabbit is { } r)
        {
            builder.UseSetting("Service:Role", "outbox-publisher");
            builder.UseSetting("RabbitMq:Host", r.Host);
            builder.UseSetting("RabbitMq:Port", r.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("RabbitMq:VirtualHost", "/");
            builder.UseSetting("RabbitMq:Username", "guest");
            builder.UseSetting("RabbitMq:Password", "guest");
            builder.UseSetting("Outbox:PollIntervalMs", "100");
        }
    }

    /// <summary>Cliente HTTP autenticado como o comerciante de desenvolvimento informado ("A" ou "B").</summary>
    public async Task<HttpClient> ClientForAsync(string merchant)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/dev/token", new { merchant });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", json.RootElement.GetProperty("access_token").GetString());
        return client;
    }
}
