using CashFlow.Application;
using CashFlow.Domain;
using CashFlow.Infrastructure.Outbox;
using CashFlow.Infrastructure.Persistence;
using CashFlowPlatform.Contracts.Events;
using CashFlowPlatform.Observability;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CashFlow.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCashFlowInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("CashFlow")
                               ?? throw new InvalidOperationException("ConnectionStrings:CashFlow não configurada.");

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());
        services.AddDbContext<CashFlowDbContext>((sp, o) => o
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npg => npg.CommandTimeout(15))
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        services.AddScoped<ICashFlowStore, EfCashFlowStore>();
        services.AddScoped<CashFlowService>();

        services.AddHealthChecks()
            .AddDbContextCheck<CashFlowDbContext>("postgres-cashflow", tags: [ObservabilityExtensions.ReadyTag]);

        var outbox = config.GetSection("Outbox").Get<OutboxOptions>() ?? new OutboxOptions();
        services.AddSingleton(outbox);

        if (outbox.PublisherEnabled)
        {
            services.AddMassTransit(x =>
            {
                x.UsingRabbitMq((ctx, cfg) =>
                {
                    ConfigureRabbitHost(cfg, config);
                    // Exchange com nome explícito, desacoplado do namespace .NET.
                    cfg.Message<CashTransactionCreatedV1>(m => m.SetEntityName(CashTransactionCreatedV1.ExchangeName));
                    cfg.Publish<CashTransactionCreatedV1>(p => p.Durable = true);
                });
            });
            services.Configure<MassTransitHostOptions>(o =>
            {
                o.WaitUntilStarted = false; // API/worker sobem mesmo com RabbitMQ fora; Outbox acumula.
                o.StopTimeout = TimeSpan.FromSeconds(15);
            });
            services.AddHostedService<OutboxPublisher>();
        }

        return services;
    }

    internal static void ConfigureRabbitHost(IRabbitMqBusFactoryConfigurator cfg, IConfiguration config)
    {
        var host = config["RabbitMq:Host"] ?? "localhost";
        var vhost = config["RabbitMq:VirtualHost"] ?? "/";
        var port = ushort.TryParse(config["RabbitMq:Port"], out var p) ? p : (ushort)5672;
        cfg.Host(host, port, vhost, h =>
        {
            h.Username(config["RabbitMq:Username"] ?? "guest");
            h.Password(config["RabbitMq:Password"] ?? "guest");
            h.PublisherConfirmation = true;
        });
    }

    /// <summary>Etapa controlada de migração (executada por um job/container dedicado, não pelas réplicas).</summary>
    public static async Task MigrateAndSeedAsync(IServiceProvider services, bool seedDevelopmentData, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CashFlowDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("CashFlow.Migrations");

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

        if (!seedDevelopmentData) return;

        foreach (var m in DevelopmentMerchants.All)
        {
            if (!await db.Merchants.AnyAsync(x => x.Id == m.Id, ct))
                db.Merchants.Add(new Merchant(m.Id, m.Name, "America/Sao_Paulo", DateTimeOffset.UtcNow));
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Comerciantes de desenvolvimento garantidos.");
    }
}

/// <summary>Comerciantes de demonstração (apenas ambiente Development).</summary>
public static class DevelopmentMerchants
{
    public sealed record DevMerchant(string Code, Guid Id, string Name);

    public static readonly DevMerchant A = new("A", Guid.Parse("11111111-1111-1111-1111-111111111111"), "Loja A (demo)");
    public static readonly DevMerchant B = new("B", Guid.Parse("22222222-2222-2222-2222-222222222222"), "Loja B (demo)");
    public static readonly IReadOnlyList<DevMerchant> All = [A, B];
}
