using CashFlowPlatform.Contracts.Events;
using DailyConsolidation.Application;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DailyConsolidation.Infrastructure.Messaging;

/// <summary>
/// Consumer MassTransit. O ACK só acontece quando Consume retorna com sucesso, ou seja, após o commit no banco.
/// Exceção → retry com backoff; esgotado ou InvalidEventException → fila _error (sem descarte silencioso).
/// </summary>
public sealed class CashTransactionCreatedConsumer(CashTransactionCreatedHandler handler, ILogger<CashTransactionCreatedConsumer> logger)
    : IConsumer<CashTransactionCreatedV1>
{
    public async Task Consume(ConsumeContext<CashTransactionCreatedV1> context)
    {
        var evt = context.Message;
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["EventId"] = evt.EventId,
            ["CorrelationId"] = evt.CorrelationId,
            ["MerchantId"] = evt.MerchantId
        });

        var result = await handler.HandleAsync(evt, context.CancellationToken);
        logger.LogInformation("Evento {EventId} {Outcome} (dias recalculados: {Days})", evt.EventId, result.Outcome, result.RecalculatedDays);
    }
}

/// <summary>Heartbeat do consumidor (somente quando o bus está saudável) — base do estado UNKNOWN na API.</summary>
internal sealed class ConsumerHeartbeatService(IServiceScopeFactory scopes, HealthCheckService health, ILogger<ConsumerHeartbeatService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                var report = await health.CheckHealthAsync(r => r.Tags.Contains("masstransit"), stoppingToken);
                if (report.Status == HealthStatus.Healthy)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IProjectionStore>()
                        .RecordHeartbeatAsync(CashTransactionCreatedHandler.ConsumerName, stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Falha ao registrar heartbeat do consumidor");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
