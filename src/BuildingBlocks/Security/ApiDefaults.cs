using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace CashFlowPlatform.Security;

public static class ApiDefaults
{
    public const string TenantRateLimitPolicy = "per-tenant";

    /// <summary>OpenAPI (/openapi/v1.json) com esquema Bearer + Swagger UI (/swagger).</summary>
    public static void AddPlatformOpenApi(this IServiceCollection services, string title)
    {
        services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info = new OpenApiInfo { Title = title, Version = "v1" };
            doc.Components ??= new OpenApiComponents();
            doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            doc.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Em desenvolvimento, obtenha um token em POST /dev/token (Cash Flow API)."
            };
            doc.Security ??= [];
            doc.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", doc)] = [] });
            return Task.CompletedTask;
        }));
    }

    public static void UsePlatformOpenApi(this WebApplication app, string title)
    {
        app.MapOpenApi().AllowAnonymous();
        app.UseSwaggerUI(o =>
        {
            o.SwaggerEndpoint("/openapi/v1.json", title);
            o.RoutePrefix = "swagger";
        });
    }

    /// <summary>Rate limit por tenant (claim merchant_id) com fallback por IP; responde 429 + Retry-After.</summary>
    public static void AddPlatformRateLimiting(this IServiceCollection services, int permitsPerSecond)
    {
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (ctx, _) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "1";
                return ValueTask.CompletedTask;
            };
            o.AddPolicy(TenantRateLimitPolicy, http =>
            {
                var partition = http.User.FindFirstValue(PlatformClaims.MerchantId)
                                ?? http.Connection.RemoteIpAddress?.ToString()
                                ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitsPerSecond,
                    Window = TimeSpan.FromSeconds(1),
                    QueueLimit = 0
                });
            });
        });
    }

    /// <summary>Correlation-Id de negócio: aceita X-Correlation-Id (GUID) ou gera um novo; devolvido na resposta.</summary>
    public static Guid GetCorrelationId(this HttpContext ctx)
    {
        if (ctx.Items.TryGetValue("CorrelationId", out var existing) && existing is Guid g) return g;
        var id = Guid.TryParse(ctx.Request.Headers["X-Correlation-Id"], out var parsed) ? parsed : Guid.CreateVersion7();
        ctx.Items["CorrelationId"] = id;
        ctx.Response.Headers["X-Correlation-Id"] = id.ToString();
        return id;
    }
}
