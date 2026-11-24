using CashFlow.Application;
using CashFlow.Domain;
using CashFlow.Infrastructure;
using CashFlowPlatform.Security;
using Microsoft.AspNetCore.Mvc;

namespace CashFlow.Api;

internal static class Endpoints
{
    public static void MapCashFlowEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1").RequireRateLimiting(ApiDefaults.TenantRateLimitPolicy);

        var accounts = v1.MapGroup("/cash-accounts").WithTags("Cash Accounts");

        accounts.MapPost("/", async (CreateCashAccountCommand body, ITenantContext tenant, CashFlowService svc, HttpContext http, CancellationToken ct) =>
            {
                var created = await svc.CreateAccountAsync(tenant.MerchantId, tenant.UserId, body, http.GetCorrelationId(), ct);
                return Results.Created($"/api/v1/cash-accounts/{created.Id}", created);
            })
            .RequireAuthorization(Scopes.AccountsManage)
            .WithSummary("Cria conta de caixa (opcionalmente com saldo de abertura auditável)")
            .Produces<CashAccountResponse>(201).ProducesProblem(400).ProducesProblem(422);

        accounts.MapGet("/", async (ITenantContext tenant, CashFlowService svc, CancellationToken ct) =>
                Results.Ok(await svc.ListAccountsAsync(tenant.MerchantId, ct)))
            .RequireAuthorization(Scopes.TransactionsRead)
            .WithSummary("Lista contas do comerciante autenticado");

        accounts.MapGet("/{id:guid}", async (Guid id, ITenantContext tenant, CashFlowService svc, CancellationToken ct) =>
                Results.Ok(await svc.GetAccountAsync(tenant.MerchantId, id, ct)))
            .RequireAuthorization(Scopes.TransactionsRead)
            .Produces<CashAccountResponse>().ProducesProblem(404);

        var tx = v1.MapGroup("/transactions").WithTags("Transactions");

        tx.MapPost("/", async (
                RegisterTransactionCommand body,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                ITenantContext tenant, CashFlowService svc, HttpContext http, CancellationToken ct) =>
            {
                if (body.CashAccountId == Guid.Empty)
                    throw DomainException.Validation("cash_account_required", "cashAccountId é obrigatório.");

                var result = await svc.RegisterTransactionAsync(tenant.MerchantId, tenant.UserId, idempotencyKey, body, http.GetCorrelationId(), ct);
                return Respond(result, http);
            })
            .RequireAuthorization(Scopes.TransactionsWrite)
            .WithSummary("Registra crédito ou débito (exige Idempotency-Key)")
            .WithDescription("Mesma chave + mesmo payload: 200 OK com o resultado original (header Idempotent-Replayed: true). Mesma chave + payload diferente: 409.")
            .Produces<TransactionResponse>(201).Produces<TransactionResponse>(200)
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);

        tx.MapGet("/{id:guid}", async (Guid id, ITenantContext tenant, CashFlowService svc, CancellationToken ct) =>
                Results.Ok(await svc.GetTransactionAsync(tenant.MerchantId, id, ct)))
            .RequireAuthorization(Scopes.TransactionsRead)
            .Produces<TransactionResponse>().ProducesProblem(404);

        tx.MapGet("/", async (
                Guid cashAccountId, DateOnly? from, DateOnly? to, int? page, int? pageSize,
                ITenantContext tenant, CashFlowService svc, CancellationToken ct) =>
            {
                var result = await svc.ListTransactionsAsync(tenant.MerchantId, cashAccountId, from, to, page ?? 1, pageSize ?? 50, ct);
                return Results.Ok(new
                {
                    items = result.Items,
                    pagination = new { result.Page, result.PageSize, result.TotalItems, result.TotalPages }
                });
            })
            .RequireAuthorization(Scopes.TransactionsRead)
            .WithSummary("Lista lançamentos por conta e período (businessDate), paginado e ordenado por occurredAt, id");

        tx.MapPost("/{id:guid}/reversals", async (
                Guid id, ReverseTransactionCommand body,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                ITenantContext tenant, CashFlowService svc, HttpContext http, CancellationToken ct) =>
            {
                var result = await svc.ReverseTransactionAsync(tenant.MerchantId, tenant.UserId, idempotencyKey, id, body, http.GetCorrelationId(), ct);
                return Respond(result, http);
            })
            .RequireAuthorization(Scopes.TransactionsWrite)
            .WithSummary("Estorno integral por lançamento compensatório (o original não é alterado)")
            .Produces<TransactionResponse>(201).ProducesProblem(404).ProducesProblem(409).ProducesProblem(422);
    }

    private static IResult Respond(IdempotentResult<TransactionResponse> result, HttpContext http)
    {
        if (result.Replayed)
        {
            http.Response.Headers["Idempotent-Replayed"] = "true";
            return Results.Ok(result.Value);
        }
        return Results.Created($"/api/v1/transactions/{result.Value.Id}", result.Value);
    }

    /// <summary>Emissão de tokens locais. Mapeado SOMENTE quando Authentication:Mode=Development em ambiente Development.</summary>
    public static void MapDevTokenEndpoints(this WebApplication app)
    {
        var dev = app.MapGroup("/dev").WithTags("Development only").AllowAnonymous();

        dev.MapGet("/merchants", () => DevelopmentMerchants.All.Select(m => new { m.Code, m.Id, m.Name }))
            .WithSummary("Comerciantes de demonstração");

        dev.MapPost("/token", (DevTokenRequest req, DevTokenIssuer issuer) =>
            {
                var merchant = DevelopmentMerchants.All.FirstOrDefault(m =>
                    string.Equals(m.Code, req.Merchant, StringComparison.OrdinalIgnoreCase) || m.Id.ToString() == req.Merchant);
                if (merchant is null)
                    return Results.Problem(statusCode: 400, title: "Comerciante desconhecido", detail: "Use 'A' ou 'B'.");

                var scopes = req.Scopes is { Length: > 0 } ? req.Scopes : Scopes.All;
                var token = issuer.Issue(merchant.Id, req.User ?? $"dev-user-{merchant.Code.ToLowerInvariant()}", scopes, TimeSpan.FromHours(8));
                return Results.Ok(new { access_token = token, token_type = "Bearer", expires_in = 8 * 3600, merchantId = merchant.Id, scopes });
            })
            .WithSummary("Emite JWT de desenvolvimento para o comerciante A ou B");
    }
}

internal sealed record DevTokenRequest(string Merchant, string? User, string[]? Scopes);
