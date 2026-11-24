using CashFlow.Api;
using CashFlow.Infrastructure;
using CashFlow.Infrastructure.Outbox;
using CashFlowPlatform.Observability;
using CashFlowPlatform.Security;

var builder = WebApplication.CreateBuilder(args);

// Papéis do mesmo executável: "api" (HTTP) ou "outbox-publisher" (worker). "--migrate" aplica migrations e encerra.
var role = builder.Configuration["Service:Role"] ?? "api";
var migrateOnly = args.Contains("--migrate");
if (string.Equals(role, "outbox-publisher", StringComparison.OrdinalIgnoreCase))
    builder.Configuration["Outbox:PublisherEnabled"] = "true";

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 16 * 1024);

builder.AddPlatformObservability(
    serviceName: role == "api" ? "cashflow-api" : "cashflow-outbox-publisher",
    activitySources: [OutboxPublisher.ActivitySourceName],
    meters: [OutboxPublisher.MeterName]);

var auth = builder.AddPlatformSecurity();
if (auth.IsDevelopmentMode) builder.Services.AddSingleton<DevTokenIssuer>();

builder.Services.AddCashFlowInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddPlatformOpenApi("Cash Flow API");
builder.Services.AddPlatformRateLimiting(builder.Configuration.GetValue("RateLimiting:PermitsPerSecond", 200));
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict);

var app = builder.Build();

if (migrateOnly)
{
    await DependencyInjection.MigrateAndSeedAsync(app.Services, seedDevelopmentData: app.Environment.IsDevelopment());
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapPlatformEndpoints();

if (role == "api")
{
    app.UsePlatformOpenApi("Cash Flow API v1");
    app.MapCashFlowEndpoints();
    if (auth.IsDevelopmentMode) app.MapDevTokenEndpoints();
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
}

app.Logger.LogInformation("Cash Flow iniciado no papel {Role}", role);
await app.RunAsync();

public partial class Program;
