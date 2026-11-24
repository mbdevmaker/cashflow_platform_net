using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CashFlow.Infrastructure.Persistence.Migrations;

/// <summary>Migration versionada com DDL explícita (constraints que o EF não expressa bem: FK composta, índice único parcial, CHECKs).</summary>
[DbContext(typeof(CashFlowDbContext))]
[Migration("20261123000001_InitialSchema")]
public sealed class InitialSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE merchants (
                id          uuid PRIMARY KEY,
                name        varchar(200) NOT NULL,
                time_zone   varchar(100) NOT NULL,
                status      varchar(30)  NOT NULL,
                created_at  timestamptz  NOT NULL
            );

            CREATE TABLE cash_accounts (
                id            uuid PRIMARY KEY,
                merchant_id   uuid NOT NULL REFERENCES merchants(id),
                name          varchar(150) NOT NULL,
                currency      char(3)      NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
                time_zone     varchar(100) NOT NULL,
                status        varchar(30)  NOT NULL,
                last_sequence bigint       NOT NULL DEFAULT 0,
                created_at    timestamptz  NOT NULL,
                CONSTRAINT uq_cash_accounts_merchant_id UNIQUE (merchant_id, id)
            );
            CREATE INDEX ix_cash_accounts_merchant ON cash_accounts (merchant_id, created_at);

            CREATE TABLE cash_transactions (
                id                      uuid PRIMARY KEY,
                merchant_id             uuid          NOT NULL,
                cash_account_id         uuid          NOT NULL,
                type                    varchar(30)   NOT NULL CHECK (type IN ('CREDIT','DEBIT','OPENING_BALANCE')),
                amount                  numeric(19,4) NOT NULL CHECK (amount > 0),
                currency                char(3)       NOT NULL,
                description             varchar(500),
                occurred_at             timestamptz   NOT NULL,
                business_date           date          NOT NULL,
                created_at              timestamptz   NOT NULL,
                account_sequence        bigint        NOT NULL,
                idempotency_key         varchar(200)  NOT NULL,
                reverses_transaction_id uuid          NULL,
                created_by              varchar(200)  NULL,
                CONSTRAINT fk_tx_account FOREIGN KEY (merchant_id, cash_account_id)
                    REFERENCES cash_accounts (merchant_id, id),
                CONSTRAINT fk_tx_reverses FOREIGN KEY (reverses_transaction_id)
                    REFERENCES cash_transactions (id),
                CONSTRAINT uq_tx_account_seq UNIQUE (cash_account_id, account_sequence)
            );

            CREATE UNIQUE INDEX uq_tx_single_reversal
                ON cash_transactions (reverses_transaction_id)
                WHERE reverses_transaction_id IS NOT NULL;

            CREATE INDEX ix_tx_listing ON cash_transactions (merchant_id, cash_account_id, occurred_at, id);
            CREATE INDEX ix_tx_bizdate ON cash_transactions (merchant_id, cash_account_id, business_date);

            -- Imutabilidade reforçada no banco: lançamentos confirmados não são editados nem apagados.
            CREATE FUNCTION fn_cash_transactions_immutable() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'cash_transactions é imutável (use estorno compensatório)';
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER trg_cash_transactions_immutable
                BEFORE UPDATE OR DELETE ON cash_transactions
                FOR EACH ROW EXECUTE FUNCTION fn_cash_transactions_immutable();

            CREATE TABLE idempotency_records (
                merchant_id       uuid         NOT NULL,
                operation         varchar(100) NOT NULL,
                idempotency_key   varchar(200) NOT NULL,
                request_hash      char(64)     NOT NULL,
                resource_id       uuid         NOT NULL,
                response_status   int          NOT NULL,
                created_at        timestamptz  NOT NULL,
                CONSTRAINT pk_idempotency_records PRIMARY KEY (merchant_id, operation, idempotency_key)
            );

            CREATE TABLE outbox_messages (
                id              uuid PRIMARY KEY,
                event_type      varchar(200) NOT NULL,
                schema_version  int          NOT NULL,
                payload         jsonb        NOT NULL,
                trace_parent    varchar(128) NULL,
                created_at      timestamptz  NOT NULL,
                published_at    timestamptz  NULL,
                retry_count     int          NOT NULL DEFAULT 0,
                next_attempt_at timestamptz  NOT NULL,
                last_error      text         NULL
            );

            CREATE INDEX ix_outbox_pending
                ON outbox_messages (next_attempt_at, created_at)
                WHERE published_at IS NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS outbox_messages;
            DROP TABLE IF EXISTS idempotency_records;
            DROP TRIGGER IF EXISTS trg_cash_transactions_immutable ON cash_transactions;
            DROP FUNCTION IF EXISTS fn_cash_transactions_immutable();
            DROP TABLE IF EXISTS cash_transactions;
            DROP TABLE IF EXISTS cash_accounts;
            DROP TABLE IF EXISTS merchants;
            """);
    }
}
