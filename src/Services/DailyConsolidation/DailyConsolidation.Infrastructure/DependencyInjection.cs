using CashFlowPlatform.Contracts.Events;
using CashFlowPlatform.Observability;
using DailyConsolidation.Application;
using DailyConsolidation.Infrastructure.Caching;
using DailyConsolidation.Infrastructure.Messaging;
using DailyConsolidation.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using StackExchange.Redis;

namespace DailyConsolidation.Infrastructure;

public static class DependencyInjection
{
    public const string QueueName = "consolidation.cash-transaction-created";

    public static IServiceCollection AddConsolidationInfrastructure(this IServiceCollection services, IConfiguration config, bool enableConsumer)
    {
        var connectionString = config.GetConnectionString("Consolidation")
                               ?? throw new InvalidOperationException("ConnectionStrings:Consolidation não configurada.");

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());
        services.AddDbContext<ConsolidationDbContext>((sp, o) => o
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        services.AddSingleton<IProjectionStore, NpgsqlProjectionStore>();
        services.AddSingleton(config.GetSection("Consistency").Get<ConsistencyOptions>() ?? new ConsistencyOptions());
        services.AddScoped<CashTransactionCreatedHandler>();
        services.AddScoped<BalanceQueryService>();

        // Redis: conexão tolerante a falhas (não aborta se indisponível; timeouts curtos para fallback rápido).
        var redisOptions = ConfigurationOptions.Parse(config.GetConnectionString("Redis") ?? "localhost:6379");
        redisOptions.AbortOnConnectFail = false;
        redisOptions.ConnectTimeout = 1000;
        redisOptions.SyncTimeout = 500;
        redisOptions.AsyncTimeout = 500;
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
        services.AddSingleton<IBalanceCache, RedisBalanceCache>();

        services.AddHealthChecks()
            .AddDbContextCheck<ConsolidationDbContext>("postgres-consolidation", tags: [ObservabilityExtensions.ReadyTag])
            .AddCheck<RedisHealthCheck>("redis", failureStatus: HealthStatus.Degraded, tags: [ObservabilityExtensions.ReadyTag]);

        if (enableConsumer)
        {
            services.AddMassTransit(x =>
            {
                x.AddConsumer<CashTransactionCreatedConsumer>();
                x.UsingRabbitMq((ctx, cfg) =>
                {
                    var port = ushort.TryParse(config["RabbitMq:Port"], out var p) ? p : (ushort)5672;
                    cfg.Host(config["RabbitMq:Host"] ?? "localhost", port, config["RabbitMq:VirtualHost"] ?? "/", h =>
                    {
                        h.Username(config["RabbitMq:Username"] ?? "guest");
                        h.Password(config["RabbitMq:Password"] ?? "guest");
                    });
                    cfg.Message<CashTransactionCreatedV1>(m => m.SetEntityName(CashTransactionCreatedV1.ExchangeName));

                    cfg.ReceiveEndpoint(QueueName, e =>
                    {
                        e.SetQuorumQueue();
                        e.PrefetchCount = 32;
                        e.ConcurrentMessageLimit = 16; // paralelismo entre contas; mesma conta serializada por advisory lock
                        e.UseMessageRetry(r =>
                        {
                            r.Exponential(6, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(500));
                            r.Ignore<InvalidEventException>(); // falha permanente → direto para a error queue
                        });
                        e.ConfigureConsumer<CashTransactionCreatedConsumer>(ctx);
                    });
                });
            });
            services.Configure<MassTransitHostOptions>(o => o.WaitUntilStarted = false);
            services.AddHostedService<ConsumerHeartbeatService>();
        }

        return services;
    }

    public static async Task MigrateAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ConsolidationDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Consolidation.Migrations");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync(ct);
                break;
            }
            catch (NpgsqlException ex) when (attempt < 10)
            {
                logger.LogWarning(ex, "Banco indisponível (tentativa {Attempt}); aguardando...", attempt);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
        logger.LogInformation("Migrations aplicadas.");
    }
}

internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"ping {latency.TotalMilliseconds:F1} ms");
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Redis indisponível (consultas seguem via PostgreSQL)");
        }
    }
}
