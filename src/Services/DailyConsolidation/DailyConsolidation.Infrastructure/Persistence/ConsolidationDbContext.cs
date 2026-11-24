using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DailyConsolidation.Infrastructure.Persistence;

/// <summary>
/// Contexto usado para migrations versionadas e health check. O acesso de dados da projeção usa SQL explícito
/// via Npgsql (<see cref="NpgsqlProjectionStore"/>) para controlar lock, upsert em lote e transação.
/// </summary>
public sealed class ConsolidationDbContext(DbContextOptions<ConsolidationDbContext> options) : DbContext(options);

[DbContext(typeof(ConsolidationDbContext))]
[Migration("20261123000001_InitialSchema")]
public sealed class InitialSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE processed_events (
                event_id      uuid         NOT NULL,
                consumer_name varchar(150) NOT NULL,
                processed_at  timestamptz  NOT NULL,
                PRIMARY KEY (event_id, consumer_name)
            );

            -- Movimentos aplicados: permitem reconstrução determinística sem acessar o banco do produtor.
            CREATE TABLE daily_movements (
                event_id                uuid          PRIMARY KEY,
                merchant_id             uuid          NOT NULL,
                cash_account_id         uuid          NOT NULL,
                transaction_id          uuid          NOT NULL UNIQUE,
                business_date           date          NOT NULL,
                currency                char(3)       NOT NULL,
                transaction_type        varchar(30)   NOT NULL CHECK (transaction_type IN ('CREDIT','DEBIT','OPENING_BALANCE')),
                amount                  numeric(19,4) NOT NULL CHECK (amount > 0),
                reverses_transaction_id uuid          NULL,
                account_sequence        bigint        NULL,
                occurred_on             timestamptz   NOT NULL,
                applied_at              timestamptz   NOT NULL
            );
            CREATE INDEX ix_mov_account_date ON daily_movements (merchant_id, cash_account_id, currency, business_date);
            CREATE INDEX ix_mov_account_seq ON daily_movements (merchant_id, cash_account_id, account_sequence)
                WHERE account_sequence IS NOT NULL;

            CREATE TABLE daily_balances (
                merchant_id      uuid          NOT NULL,
                cash_account_id  uuid          NOT NULL,
                business_date    date          NOT NULL,
                currency         char(3)       NOT NULL,
                opening_balance  numeric(19,4) NOT NULL,
                total_credits    numeric(19,4) NOT NULL,
                total_debits     numeric(19,4) NOT NULL,
                closing_balance  numeric(19,4) NOT NULL,
                updated_at       timestamptz   NOT NULL,
                PRIMARY KEY (merchant_id, cash_account_id, business_date, currency),
                CONSTRAINT ck_balance_formula CHECK (closing_balance = opening_balance + total_credits - total_debits)
            );

            CREATE TABLE account_watermarks (
                merchant_id            uuid        NOT NULL,
                cash_account_id        uuid        NOT NULL,
                currency               char(3)     NOT NULL,
                contiguous_sequence    bigint      NOT NULL DEFAULT 0,
                max_seen_sequence      bigint      NOT NULL DEFAULT 0,
                last_event_occurred_on timestamptz NULL,
                last_applied_at        timestamptz NULL,
                last_lag_seconds       float8      NULL,
                PRIMARY KEY (merchant_id, cash_account_id)
            );

            CREATE TABLE consumer_heartbeats (
                consumer_name varchar(150) NOT NULL,
                instance_id   varchar(200) NOT NULL,
                last_seen_at  timestamptz  NOT NULL,
                PRIMARY KEY (consumer_name, instance_id)
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS consumer_heartbeats;
            DROP TABLE IF EXISTS account_watermarks;
            DROP TABLE IF EXISTS daily_balances;
            DROP TABLE IF EXISTS daily_movements;
            DROP TABLE IF EXISTS processed_events;
            """);
    }
}
