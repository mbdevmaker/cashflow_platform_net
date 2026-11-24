using CashFlow.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace CashFlow.Api;

/// <summary>Converte exceções em ProblemDetails (RFC 9457) sem dados sensíveis; traceId é adicionado pelo ASP.NET Core.</summary>
internal sealed class ProblemExceptionHandler(IProblemDetailsService problems, ILogger<ProblemExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, title, code, detail) = exception switch
        {
            DomainException { Kind: DomainErrorKind.Validation } d => (400, "Requisição inválida", d.Code, d.Message),
            DomainException { Kind: DomainErrorKind.NotFound } d => (404, "Recurso não encontrado", d.Code, d.Message),
            DomainException { Kind: DomainErrorKind.Conflict } d => (409, "Conflito", d.Code, d.Message),
            DomainException d => (422, "Regra de negócio não atendida", d.Code, d.Message),
            BadHttpRequestException b => (b.StatusCode, "Requisição inválida", "bad_request", "Payload inválido ou mal formado."),
            UnauthorizedAccessException => (401, "Não autenticado", "unauthenticated", "Credenciais ausentes ou inválidas."),
            NpgsqlException or TimeoutException => (503, "Dependência indisponível", "dependency_unavailable",
                "Banco de dados indisponível no momento. Tente novamente com a mesma Idempotency-Key."),
            _ => (500, "Erro inesperado", "internal_error", "Ocorreu um erro inesperado.")
        };

        if (status >= 500) logger.LogError(exception, "Falha ao processar {Method} {Path}", http.Request.Method, http.Request.Path);

        http.Response.StatusCode = status;
        var pd = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = $"https://cashflow.local/problems/{code}",
            Instance = http.Request.Path
        };
        pd.Extensions["code"] = code;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = pd, Exception = exception });
    }
}
