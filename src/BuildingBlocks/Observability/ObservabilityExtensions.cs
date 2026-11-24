using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CashFlowPlatform.Observability;

public static class ObservabilityExtensions
{
    public const string ReadyTag = "ready";

    /// <summary>
    /// Logs JSON estruturados com TraceId/SpanId, traces e métricas OpenTelemetry.
    /// OTLP é ativado quando OTEL_EXPORTER_OTLP_ENDPOINT está configurado; métricas também ficam em /metrics (Prometheus).
    /// </summary>
    public static void AddPlatformObservability(
        this WebApplicationBuilder builder, string serviceName, string[] activitySources, string[] meters)
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var useOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
            o.JsonWriterOptions = new JsonWriterOptions { Indented = false };
        });
        builder.Logging.Configure(o => o.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: "1.0.0")
                .AddAttributes([new("deployment.environment", builder.Environment.EnvironmentName)]))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation(o =>
                        o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health")
                                          && !ctx.Request.Path.StartsWithSegments("/metrics"))
                    .AddHttpClientInstrumentation()
                    .AddNpgsql()
                    .AddSource("MassTransit")
                    .AddSource(activitySources);
                if (useOtlp) t.AddOtlpExporter();
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("MassTransit", "Npgsql")
                    .AddMeter(meters)
                    .AddPrometheusExporter();
                if (useOtlp) m.AddOtlpExporter();
            });

        if (useOtlp)
        {
            builder.Logging.AddOpenTelemetry(l =>
            {
                l.IncludeScopes = true;
                l.IncludeFormattedMessage = true;
            });
            otel.WithLogging(l => l.AddOtlpExporter());
        }

    }

    /// <summary>/health/live (processo vivo) e /health/ready (dependências tagueadas "ready"), além de /metrics.</summary>
    public static WebApplication MapPlatformEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteHealthAsync
        }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains(ReadyTag),
            ResponseWriter = WriteHealthAsync
        }).AllowAnonymous();
        app.MapPrometheusScrapingEndpoint("/metrics").AllowAnonymous();
        return app;
    }

    private static Task WriteHealthAsync(HttpContext ctx, HealthReport report)
    {
        ctx.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), e.Value.Description })
        };
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(body));
    }

    public static string? GetConnectionStringOrThrow(this IConfiguration cfg, string name) =>
        cfg.GetConnectionString(name) ?? throw new InvalidOperationException($"ConnectionStrings:{name} não configurada.");
}
