using Npgsql;

namespace FreshDays;

public static class PostgresMigrations
{
    public static async Task Apply(NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Apply(transaction);
        await transaction.CommitAsync();
    }

    public static async Task Apply(NpgsqlTransaction transaction)
    {
        var connection = transaction.Connection!;
        await using var setup = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(12685, 6);
            CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY);
            """, connection, transaction);
        await setup.ExecuteNonQueryAsync();
        await ApplyVersion(connection, transaction, 1, InitialSchema);
        await ApplyVersion(connection, transaction, 2, IdentitySchema);
        await ApplyVersion(connection, transaction, 3, InvitationSchema);
    }

    private static async Task ApplyVersion(NpgsqlConnection connection, NpgsqlTransaction transaction, int version, string sql)
    {
        await using var record = new NpgsqlCommand("""
            INSERT INTO schema_migrations (version) VALUES (@version)
            ON CONFLICT DO NOTHING RETURNING version;
            """, connection, transaction);
        record.Parameters.AddWithValue("version", version);
        if (await record.ExecuteScalarAsync() is null)
            return;

        await using var migrate = new NpgsqlCommand(sql, connection, transaction);
        await migrate.ExecuteNonQueryAsync();
    }

    private const string InitialSchema = """
        CREATE TABLE households (
            id UUID PRIMARY KEY,
            name TEXT NOT NULL
        );
        INSERT INTO households (id, name)
        VALUES ('00000000-0000-0000-0000-000000000001', '鮮日子');

        CREATE TABLE inventory_items (
            household_id UUID NOT NULL REFERENCES households(id) ON DELETE CASCADE,
            id TEXT NOT NULL,
            name TEXT NOT NULL,
            position INTEGER NOT NULL,
            expires_at JSONB,
            extra JSONB NOT NULL DEFAULT '{}'::jsonb,
            version BIGINT NOT NULL DEFAULT 1,
            PRIMARY KEY (household_id, id)
        );

        CREATE TABLE shopping_items (
            household_id UUID NOT NULL REFERENCES households(id) ON DELETE CASCADE,
            id TEXT NOT NULL,
            name TEXT NOT NULL,
            position INTEGER NOT NULL,
            completed JSONB,
            extra JSONB NOT NULL DEFAULT '{}'::jsonb,
            version BIGINT NOT NULL DEFAULT 1,
            PRIMARY KEY (household_id, id)
        );

        CREATE TABLE line_subscriptions (
            household_id UUID NOT NULL REFERENCES households(id) ON DELETE CASCADE,
            user_id TEXT NOT NULL,
            PRIMARY KEY (household_id, user_id)
        );

        CREATE TABLE line_notifications (
            household_id UUID NOT NULL REFERENCES households(id) ON DELETE CASCADE,
            day DATE NOT NULL,
            user_id TEXT NOT NULL,
            retry_key UUID NOT NULL,
            sent BOOLEAN NOT NULL DEFAULT FALSE,
            PRIMARY KEY (household_id, day, user_id)
        );
        """;

    private const string IdentitySchema = """
        CREATE TABLE users (
            id UUID PRIMARY KEY,
            google_sub TEXT NOT NULL UNIQUE,
            email TEXT NOT NULL,
            google_email_authoritative BOOLEAN NOT NULL DEFAULT FALSE
        );

        CREATE TABLE household_members (
            household_id UUID NOT NULL REFERENCES households(id) ON DELETE CASCADE,
            user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            role TEXT NOT NULL CHECK (role IN ('owner', 'member')),
            PRIMARY KEY (household_id, user_id)
        );
        CREATE UNIQUE INDEX household_single_owner ON household_members(household_id) WHERE role = 'owner';
        CREATE INDEX household_members_user ON household_members(user_id, household_id);

        CREATE TABLE legacy_household_claim (
            singleton BOOLEAN PRIMARY KEY CHECK (singleton),
            claimed BOOLEAN NOT NULL DEFAULT FALSE
        );
        INSERT INTO legacy_household_claim (singleton) VALUES (TRUE);

        CREATE TABLE app_sessions (
            token_hash BYTEA PRIMARY KEY,
            user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            expires_at TIMESTAMPTZ NOT NULL
        );
        CREATE INDEX app_sessions_user ON app_sessions(user_id);
        CREATE INDEX app_sessions_expiry ON app_sessions(expires_at);
        """;

    private const string InvitationSchema = """
        CREATE TABLE household_invitations (
            id UUID PRIMARY KEY,
            household_id UUID NOT NULL REFERENCES households(id) ON DELETE CASCADE,
            email TEXT NOT NULL,
            token_hash BYTEA NOT NULL UNIQUE,
            expires_at TIMESTAMPTZ NOT NULL,
            redeemed_at TIMESTAMPTZ,
            revoked_at TIMESTAMPTZ
        );
        CREATE INDEX household_invitations_household ON household_invitations(household_id, expires_at, id);
        """;
}
