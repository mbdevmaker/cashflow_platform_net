using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CashFlowPlatform.Security;

public static class Scopes
{
    public const string AccountsManage = "accounts:manage";
    public const string TransactionsWrite = "transactions:write";
    public const string TransactionsRead = "transactions:read";
    public const string BalancesRead = "balances:read";

    public static readonly string[] All = [AccountsManage, TransactionsWrite, TransactionsRead, BalancesRead];
}

public static class PlatformClaims
{
    public const string MerchantId = "merchant_id";
    public const string Scope = "scope";
    public const string Subject = "sub";
}

/// <summary>Tenant resolvido exclusivamente a partir de claims de um token validado (nunca de body/query/header livre).</summary>
public interface ITenantContext
{
    Guid MerchantId { get; }
    string? UserId { get; }
}

internal sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    public Guid MerchantId
    {
        get
        {
            var value = accessor.HttpContext?.User.FindFirstValue(PlatformClaims.MerchantId);
            return Guid.TryParse(value, out var id)
                ? id
                : throw new UnauthorizedAccessException("Token sem merchant_id válido.");
        }
    }

    public string? UserId => accessor.HttpContext?.User.FindFirstValue(PlatformClaims.Subject);
}

public sealed class AuthenticationSettings
{
    /// <summary>"Jwt" (OIDC/produção) ou "Development" (tokens emitidos localmente; bloqueado fora de Development).</summary>
    public string Mode { get; set; } = "Jwt";
    public string? Authority { get; set; }
    public string Issuer { get; set; } = "cashflow-dev";
    public string Audience { get; set; } = "cashflow-platform";
    public string? DevSigningKey { get; set; }

    public bool IsDevelopmentMode => string.Equals(Mode, "Development", StringComparison.OrdinalIgnoreCase);
}

public static class SecurityExtensions
{
    public static AuthenticationSettings AddPlatformSecurity(this WebApplicationBuilder builder)
    {
        var settings = builder.Configuration.GetSection("Authentication").Get<AuthenticationSettings>() ?? new AuthenticationSettings();

        if (settings.IsDevelopmentMode && !builder.Environment.IsDevelopment())
        {
            // Salvaguarda: o modo de desenvolvimento é impossível de ativar em outro ambiente.
            throw new InvalidOperationException(
                "Authentication:Mode=Development só é permitido com ASPNETCORE_ENVIRONMENT=Development.");
        }

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
        builder.Services.AddSingleton(settings);

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = settings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = PlatformClaims.Subject
                };

                if (settings.IsDevelopmentMode)
                {
                    o.RequireHttpsMetadata = false;
                    o.TokenValidationParameters.IssuerSigningKey = DevSigningKey(settings);
                }
                else
                {
                    o.Authority = settings.Authority
                                  ?? throw new InvalidOperationException("Authentication:Authority obrigatório no modo Jwt.");
                }
            });

        builder.Services.AddAuthorization(o =>
        {
            foreach (var scope in Scopes.All)
            {
                o.AddPolicy(scope, p => p.RequireAuthenticatedUser()
                    .RequireClaim(PlatformClaims.MerchantId)
                    .RequireAssertion(ctx => HasScope(ctx.User, scope)));
            }
        });

        return settings;
    }

    private static bool HasScope(ClaimsPrincipal user, string scope) =>
        user.FindAll(PlatformClaims.Scope)
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);

    internal static SymmetricSecurityKey DevSigningKey(AuthenticationSettings s)
    {
        var key = s.DevSigningKey;
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
            throw new InvalidOperationException("Authentication:DevSigningKey deve ter ao menos 32 bytes.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
    }
}

/// <summary>
/// Emissor de tokens APENAS para desenvolvimento local. Registrado somente quando Mode=Development
/// (que por sua vez só é aceito em ambiente Development).
/// </summary>
public sealed class DevTokenIssuer(AuthenticationSettings settings)
{
    public string Issue(Guid merchantId, string subject, IEnumerable<string> scopes, TimeSpan lifetime)
    {
        if (!settings.IsDevelopmentMode)
            throw new InvalidOperationException("Emissor de token de desenvolvimento desativado.");

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            Expires = DateTime.UtcNow.Add(lifetime),
            Claims = new Dictionary<string, object>
            {
                [PlatformClaims.Subject] = subject,
                [PlatformClaims.MerchantId] = merchantId.ToString(),
                [PlatformClaims.Scope] = string.Join(' ', scopes)
            },
            SigningCredentials = new SigningCredentials(SecurityExtensions.DevSigningKey(settings), SecurityAlgorithms.HmacSha256)
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
