using System.Diagnostics.Metrics;
using System.Text.Json;
using DailyConsolidation.Application;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace DailyConsolidation.Infrastructure.Caching;

/// <summary>
/// Cache-aside versionado (docs/architecture/07-modelo-dados.md §3). Chaves sempre incluem o MerchantId.
/// Redis é descartável: qualquer falha vira "miss" e a consulta segue para o PostgreSQL.
/// </summary>
internal sealed class RedisBalanceCache(IConnectionMultiplexer redis, ILogger<RedisBalanceCache> logger) : IBalanceCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Meter Meter = new("DailyConsolidation.Cache");
    private static readonly Counter<long> Hits = Meter.CreateCounter<long>("cache.hits");
    private static readonly Counter<long> Misses = Meter.CreateCounter<long>("cache.misses");
    private static readonly Counter<long> Errors = Meter.CreateCounter<long>("cache.errors");

    public static string VersionKey(Guid merchantId, Guid accountId) => $"daily-balance-ver:{merchantId}:{accountId}";

    public static string RangeKey(Guid merchantId, Guid accountId, DateOnly from, DateOnly to, string currency, long version) =>
        $"daily-balance-range:{merchantId}:{accountId}:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}:{currency}:v{version}";

    public async Task<BalancesSnapshot?> GetAsync(Guid merchantId, Guid cashAccountId, string currency, DateOnly from, DateOnly to, CancellationToken ct)
    {
        try
        {
            var db = redis.GetDatabase();
            var version = (long?)await db.StringGetAsync(VersionKey(merchantId, cashAccountId)) ?? 0;
            var value = await db.StringGetAsync(RangeKey(merchantId, cashAccountId, from, to, currency, version));
            if (value.IsNullOrEmpty)
            {
                Misses.Add(1);
                return null;
            }
            Hits.Add(1);
            return JsonSerializer.Deserialize<BalancesSnapshot>(value.ToString(), Json);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            Errors.Add(1);
            logger.LogDebug(ex, "Redis indisponível na leitura; fallback para PostgreSQL");
            return null;
        }
    }

    public async Task SetAsync(Guid merchantId, Guid cashAccountId, DateOnly from, DateOnly to, BalancesSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            var db = redis.GetDatabase();
            var version = (long?)await db.StringGetAsync(VersionKey(merchantId, cashAccountId)) ?? 0;
            await db.StringSetAsync(RangeKey(merchantId, cashAccountId, from, to, snapshot.Currency, version),
                JsonSerializer.Serialize(snapshot, Json), Ttl);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            Errors.Add(1);
            logger.LogDebug(ex, "Redis indisponível na escrita do cache");
        }
    }

    public async Task InvalidateAccountAsync(Guid merchantId, Guid cashAccountId, CancellationToken ct)
    {
        try
        {
            await redis.GetDatabase().StringIncrementAsync(VersionKey(merchantId, cashAccountId));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            Errors.Add(1);
            // Sem a invalidação, entradas antigas expiram pelo TTL (60 s).
            logger.LogWarning(ex, "Falha ao invalidar cache da conta; entradas expiram por TTL");
        }
    }
}
