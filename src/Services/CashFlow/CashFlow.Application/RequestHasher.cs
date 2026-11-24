using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CashFlow.Application;

/// <summary>Hash SHA-256 de uma representação canônica do payload (ordem fixa, cultura invariante).</summary>
internal static class RequestHasher
{
    public static string Hash(params object?[] parts)
    {
        var canonical = string.Join('\u001f', parts.Select(Canonical));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Canonical(object? value) => value switch
    {
        null => "∅",
        decimal d => d.ToString("0.0000", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        string s => s.Trim(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };
}
