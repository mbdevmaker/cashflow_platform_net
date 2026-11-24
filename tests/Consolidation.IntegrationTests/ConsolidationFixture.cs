using System.Net.Http.Headers;
using CashFlowPlatform.Contracts.Events;
using CashFlowPlatform.Security;
using DailyConsolidation.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Consolidation.IntegrationTests;

/// <summary>
/// PostgreSQL, Redis e RabbitMQ reais (Testcontainers), compartilhados pela coleção. Requer Docker em execução.
/// </summary>
public sealed class ConsolidationFixture : IAsyncLifetime
{
    public const string SigningKey = "integration-tests-signing-key-0123456789abcdef";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.12-alpine")
        .WithDatabase("consolidation")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:8.8-alpine").Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.3.8-management-alpine")
        .WithUsername("guest")
        .WithPassword("guest")
        .Build();

    public ConsolidationApiFactory Api { get; private set; } = default!;
    public string ConnectionString => _postgres.GetConnectionString();
    public string RedisConnectionString => _redis.GetConnectionString();
    public string RabbitConnectionString => _rabbit.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _rabbit.StartAsync());
        Api = new ConsolidationApiFactory(ConnectionString, RedisConnectionString);
        await DependencyInjection.MigrateAsync(Api.Services);
    }

    public async Task DisposeAsync()
    {
        await Api.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }

    /// <summary>Instância no papel consumer, ligada ao RabbitMQ do container.</summary>
    public ConsolidationApiFactory CreateConsumer()
    {
        var uri = new Uri(RabbitConnectionString);
        return new ConsolidationApiFactory(ConnectionString, RedisConnectionString, (uri.Host, uri.Port));
    }

    /// <summary>API com Redis apontando para uma porta sem servidor: simula o cache fora do ar.</summary>
    public ConsolidationApiFactory CreateApiWithoutRedis() => new(ConnectionString, "127.0.0.1:1");

    public static CashTransactionCreatedV1 Event(
        Guid merchantId, Guid accountId, DateOnly businessDate, decimal amount, string type = "CREDIT", long? sequence = 1, string currency = "BRL") => new()
    {
        EventId = Guid.NewGuid(),
        OccurredOn = DateTimeOffset.UtcNow,
        MerchantId = merchantId,
        CashAccountId = accountId,
        TransactionId = Guid.NewGuid(),
        TransactionType = type,
        Amount = amount,
        Currency = currency,
        BusinessDate = businessDate,
        AccountSequence = sequence,
        CorrelationId = Guid.NewGuid()
    };
}

[CollectionDefinition(Name)]
public sealed class ConsolidationCollection : ICollectionFixture<ConsolidationFixture>
{
    public const string Name = "consolidation-integration";
}

public sealed class ConsolidationApiFactory(string connectionString, string redis, (string Host, int Port)? rabbit = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Consolidation", connectionString);
        builder.UseSetting("ConnectionStrings:Redis", redis);
        builder.UseSetting("Authentication:Mode", "Development");
        builder.UseSetting("Authentication:DevSigningKey", ConsolidationFixture.SigningKey);
        builder.UseSetting("RateLimiting:PermitsPerSecond", "100000");

        if (rabbit is { } r)
        {
            builder.UseSetting("Service:Role", "consumer");
            builder.UseSetting("RabbitMq:Host", r.Host);
            builder.UseSetting("RabbitMq:Port", r.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("RabbitMq:VirtualHost", "/");
            builder.UseSetting("RabbitMq:Username", "guest");
            builder.UseSetting("RabbitMq:Password", "guest");
        }
    }

    /// <summary>Cliente HTTP com JWT de desenvolvimento para o comerciante informado (escopo balances:read).</summary>
    public HttpClient ClientFor(Guid merchantId)
    {
        var issuer = Services.GetRequiredService<AuthenticationSettings>();
        var token = new DevTokenIssuer(issuer).Issue(merchantId, "it-user", [Scopes.BalancesRead], TimeSpan.FromMinutes(10));
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
