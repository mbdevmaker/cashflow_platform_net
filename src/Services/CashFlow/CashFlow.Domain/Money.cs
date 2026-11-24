namespace CashFlow.Domain;

public static class Money
{
    /// <summary>Maior valor representável em NUMERIC(19,4).</summary>
    public const decimal MaxAmount = 999_999_999_999_999.9999m;

    public static readonly IReadOnlySet<string> SupportedCurrencies = new HashSet<string>(StringComparer.Ordinal) { "BRL" };

    public static void EnsureValidAmount(decimal amount)
    {
        if (amount <= 0)
            throw DomainException.Validation("amount_not_positive", "O valor deve ser maior que zero.");
        if (amount > MaxAmount)
            throw DomainException.Validation("amount_too_large", "O valor excede o limite permitido.");
        if (decimal.Round(amount, 4) != amount)
            throw DomainException.Validation("amount_precision", "O valor aceita no máximo 4 casas decimais.");
    }

    public static string NormalizeCurrency(string? currency)
    {
        var code = (currency ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length != 3 || !code.All(char.IsAsciiLetterUpper))
            throw DomainException.Validation("currency_invalid", "Moeda deve ser um código ISO 4217 de 3 letras.");
        if (!SupportedCurrencies.Contains(code))
            throw DomainException.Validation("currency_not_supported", $"Moeda '{code}' não suportada nesta versão (apenas BRL).");
        return code;
    }
}
