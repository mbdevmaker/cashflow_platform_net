using CashFlow.Application;
using CashFlow.Domain;
using Microsoft.EntityFrameworkCore;

namespace CashFlow.Infrastructure.Persistence;

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string? TraceParent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Mapeamento EF Core. O schema (constraints, índices parciais, FK composta) é criado pela migration
/// <see cref="Migrations.InitialSchema"/> com DDL explícita, espelhando docs/architecture/07-modelo-dados.md.
/// </summary>
public sealed class CashFlowDbContext(DbContextOptions<CashFlowDbContext> options) : DbContext(options)
{
    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<CashAccount> CashAccounts => Set<CashAccount>();
    public DbSet<CashTransaction> CashTransactions => Set<CashTransaction>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var b = modelBuilder;
        b.Entity<Merchant>(e =>
        {
            e.ToTable("merchants");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.IsActive);
        });

        b.Entity<CashAccount>(e =>
        {
            e.ToTable("cash_accounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
        });

        b.Entity<CashTransaction>(e =>
        {
            e.ToTable("cash_transactions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Amount).HasPrecision(19, 4);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Ignore(x => x.Status);
            e.Ignore(x => x.IsReversal);
        });

        b.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("idempotency_records");
            e.HasKey(x => new { x.MerchantId, x.Operation, x.IdempotencyKey });
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Payload).HasColumnType("jsonb");
        });
    }
}
