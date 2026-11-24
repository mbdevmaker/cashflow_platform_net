using CashFlowPlatform.Observability;
using CashFlowPlatform.Security;
using DailyConsolidation.Application;
using DailyConsolidation.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Papéis: "api" (consultas) ou "consumer" (worker RabbitMQ). "--migrate" e "--rebuild-projections" são etapas operacionais.
var role = builder.Configuration["Service:Role"] ?? "api";
var isConsumer = string.Equals(role, "consumer", StringComparison.OrdinalIgnoreCase);

builder.AddPlatformObservability(
    serviceName: isConsumer ? "daily-consolidation-consumer" : "daily-consolidation-api",
    activitySources: [],
    meters: [CashTransactionCreatedHandler.MeterName, "DailyConsolidation.Cache"]);

builder.AddPlatformSecurity();
builder.Services.AddConsolidationInfrastructure(builder.Configuration, enableConsumer: isConsumer);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ConsolidationExceptionHandler>();
builder.Services.AddPlatformOpenApi("Daily Consolidation API");
builder.Services.AddPlatformRateLimiting(builder.Configuration.GetValue("RateLimiting:PermitsPerSecond", 500));

var app = builder.Build();

if (args.Contains("--migrate"))
{
    await DependencyInjection.MigrateAsync(app.Services);
    return;
}
if (args.Contains("--rebuild-projections"))
{
    var rebuilt = await app.Services.GetRequiredService<IProjectionStore>().RebuildAllAsync(CancellationToken.None);
    app.Logger.LogInformation("Projeção reconstruída para {Accounts} conta(s).", rebuilt);
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapPlatformEndpoints();

if (!isConsumer)
{
    app.UsePlatformOpenApi("Daily Consolidation API v1");
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

    var balances = app.MapGroup("/api/v1/daily-balances")
        .WithTags("Daily Balances")
        .RequireAuthorization(Scopes.BalancesRead)
        .RequireRateLimiting(ApiDefaults.TenantRateLimitPolicy);

    balances.MapGet("/", async (Guid cashAccountId, DateOnly from, DateOnly to, ITenantContext tenant, BalanceQueryService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetPeriodAsync(tenant.MerchantId, cashAccountId, from, to, ct)))
        .WithSummary("Consolidado por período (inclui todos os dias, com e sem movimento)")
        .Produces<PeriodBalanceResponse>().ProducesProblem(400).ProducesProblem(404);

    balances.MapGet("/status", async (Guid cashAccountId, ITenantContext tenant, BalanceQueryService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetStatusAsync(tenant.MerchantId, cashAccountId, ct)))
        .WithSummary("Estado da projeção: CURRENT | PROCESSING | DELAYED | UNKNOWN")
        .Produces<ProjectionStatusResponse>().ProducesProblem(404);

    balances.MapGet("/{businessDate}", async (DateOnly businessDate, Guid cashAccountId, ITenantContext tenant, BalanceQueryService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetDayAsync(tenant.MerchantId, cashAccountId, businessDate, ct)))
        .WithSummary("Consolidado de um dia")
        .Produces<DayBalanceResponse>().ProducesProblem(404);
}

app.Logger.LogInformation("Daily Consolidation iniciado no papel {Role}", role);
await app.RunAsync();

internal sealed class ConsolidationExceptionHandler(IProblemDetailsService problems, ILogger<ConsolidationExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, title, code, detail) = exception switch
        {
            ValidationException v => (400, "Requisição inválida", v.Code, v.Message),
            NotFoundException n => (404, "Recurso não encontrado", "account_not_found", n.Message),
            BadHttpRequestException => (400, "Requisição inválida", "bad_request", "Parâmetros inválidos ou ausentes."),
            UnauthorizedAccessException => (401, "Não autenticado", "unauthenticated", "Credenciais ausentes ou inválidas."),
            NpgsqlException or TimeoutException => (503, "Dependência indisponível", "dependency_unavailable", "Banco da consolidação indisponível."),
            _ => (500, "Erro inesperado", "internal_error", "Ocorreu um erro inesperado.")
        };
        if (status >= 500) logger.LogError(exception, "Falha ao processar {Path}", http.Request.Path);

        http.Response.StatusCode = status;
        var pd = new ProblemDetails { Status = status, Title = title, Detail = detail, Type = $"https://cashflow.local/problems/{code}", Instance = http.Request.Path };
        pd.Extensions["code"] = code;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = pd, Exception = exception });
    }
}

public partial class Program;
