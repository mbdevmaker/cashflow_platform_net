using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using CashFlow.Infrastructure.Persistence;
using CashFlowPlatform.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CashFlow.Infrastructure.Outbox;

public sealed class OutboxOptions
{
    public bool PublisherEnabled { get; set; }
    public int BatchSize { get; set; } = 100;
    public int PollIntervalMs { get; set; } = 500;
    public int LeaseSeconds { get; set; } = 30;
    public int PublishTimeoutSeconds { get; set; } = 10;
    public int MaxBackoffSeconds { get; set; } = 60;
}

/// <summary>
/// Publica mensagens da Outbox no RabbitMQ com publisher confirms.
/// Claim concorrente seguro: UPDATE ... WHERE id IN (SELECT ... FOR UPDATE SKIP LOCKED) define um "lease"
/// (next_attempt_at no futuro) e a transação é encerrada antes da chamada de rede.
/// A linha só recebe published_at após a confirmação do broker; se o processo cair entre confirmação e marcação,
/// a mensagem é republicada e o consumidor a descarta por EventId (at-least-once + consumidor idempotente).
/// </summary>
public sealed class OutboxPublisher(
    NpgsqlDataSource dataSource,
    IBus bus,
    OutboxOptions options,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    public const string ActivitySourceName = "CashFlow.Outbox";
    public const string MeterName = "CashFlow.Outbox";

    private static readonly ActivitySource Source = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Published = Meter.CreateCounter<long>("outbox.published", description: "Mensagens publicadas");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("outbox.publish_failures", description: "Falhas de publicação");

    private static long _pending;
    private static double _oldestAgeSeconds;

    static OutboxPublisher()
    {
        Meter.CreateObservableGauge("outbox.pending", () => Interlocked.Read(ref _pending), description: "Mensagens pendentes na Outbox");
        Meter.CreateObservableGauge("outbox.oldest_age_seconds", () => Volatile.Read(ref _oldestAgeSeconds), unit: "s",
            description: "Idade da mensagem pendente mais antiga");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox publisher iniciado (batch={Batch}, poll={Poll}ms)", options.BatchSize, options.PollIntervalMs);
        var lastStats = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            var published = 0;
            try
            {
                if (DateTime.UtcNow - lastStats > TimeSpan.FromSeconds(5))
                {
                    await RefreshStatsAsync(stoppingToken);
                    lastStats = DateTime.UtcNow;
                }

                var batch = await ClaimAsync(stoppingToken);
                foreach (var msg in batch)
                {
                    if (await PublishOneAsync(msg, stoppingToken)) published++;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha no ciclo do Outbox publisher; nova tentativa em seguida");
                await DelaySafe(TimeSpan.FromSeconds(2), stoppingToken);
            }

            if (published == 0)
                await DelaySafe(TimeSpan.FromMilliseconds(options.PollIntervalMs), stoppingToken);
        }
    }

    private sealed record ClaimedMessage(Guid Id, string Payload, string? TraceParent, int RetryCount);

    private async Task<List<ClaimedMessage>> ClaimAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE outbox_messages o
               SET next_attempt_at = now() + make_interval(secs => @lease)
             WHERE o.id IN (
                   SELECT id FROM outbox_messages
                    WHERE published_at IS NULL AND next_attempt_at <= now()
                    ORDER BY created_at
                    LIMIT @batch
                    FOR UPDATE SKIP LOCKED)
            RETURNING o.id, o.payload::text, o.trace_parent, o.retry_count, o.created_at
            """);
        cmd.Parameters.AddWithValue("lease", (double)options.LeaseSeconds);
        cmd.Parameters.AddWithValue("batch", options.BatchSize);

        var list = new List<(ClaimedMessage Msg, DateTime CreatedAt)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add((new ClaimedMessage(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3)), reader.GetDateTime(4)));
        }
        return list.OrderBy(x => x.CreatedAt).Select(x => x.Msg).ToList();
    }

    private async Task<bool> PublishOneAsync(ClaimedMessage msg, CancellationToken ct)
    {
        ActivityContext.TryParse(msg.TraceParent, null, out var parent);
        using var activity = Source.StartActivity("outbox publish", ActivityKind.Producer, parent);
        activity?.SetTag("messaging.message.id", msg.Id);

        try
        {
            var evt = JsonSerializer.Deserialize<CashTransactionCreatedV1>(msg.Payload, EfCashFlowStore.EventJson)
                      ?? throw new InvalidOperationException("Payload vazio na Outbox.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.PublishTimeoutSeconds));

            // MassTransit + RabbitMQ: mensagem persistente e Publish só completa após o publisher confirm do broker.
            await bus.Publish(evt, ctx =>
            {
                ctx.MessageId = evt.EventId;
                ctx.CorrelationId = evt.CorrelationId;
                ctx.Durable = true;
                ctx.Headers.Set("schema-version", evt.SchemaVersion);
                ctx.Headers.Set("event-type", evt.EventType);
            }, timeout.Token);

            await using var mark = dataSource.CreateCommand(
                "UPDATE outbox_messages SET published_at = now(), last_error = NULL WHERE id = @id");
            mark.Parameters.AddWithValue("id", msg.Id);
            await mark.ExecuteNonQueryAsync(ct);

            Published.Add(1);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Failures.Add(1);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            var backoff = Math.Min(options.MaxBackoffSeconds, Math.Pow(2, Math.Min(msg.RetryCount + 1, 10)));
            logger.LogWarning(ex, "Falha ao publicar mensagem {MessageId} (tentativa {Attempt}); nova tentativa em {Backoff}s",
                msg.Id, msg.RetryCount + 1, backoff);

            await using var fail = dataSource.CreateCommand("""
                UPDATE outbox_messages
                   SET retry_count = retry_count + 1,
                       last_error = left(@err, 2000),
                       next_attempt_at = now() + make_interval(secs => @backoff)
                 WHERE id = @id AND published_at IS NULL
                """);
            fail.Parameters.AddWithValue("err", ex.Message);
            fail.Parameters.AddWithValue("backoff", backoff);
            fail.Parameters.AddWithValue("id", msg.Id);
            await fail.ExecuteNonQueryAsync(CancellationToken.None);
            return false;
        }
    }

    private async Task RefreshStatsAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT count(*), COALESCE(EXTRACT(EPOCH FROM now() - min(created_at)), 0)::float8
              FROM outbox_messages WHERE published_at IS NULL
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            Interlocked.Exchange(ref _pending, reader.GetInt64(0));
            Volatile.Write(ref _oldestAgeSeconds, reader.GetDouble(1));
        }
    }

    private static async Task DelaySafe(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }
}
