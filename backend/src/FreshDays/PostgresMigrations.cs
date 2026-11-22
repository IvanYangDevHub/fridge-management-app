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
        await ApplyVersion(connection, transaction, 4, LineHouseholdSchema);
        await ApplyVersion(connection, transaction, 5, AttributionIndexes);
        await ApplyVersion(connection, transaction, 6, LineDeliveryAndInvitationPrivacy);
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

    private const string AttributionIndexes = """
        CREATE INDEX inventory_created_by ON inventory_items USING hash (lower(extra->>'createdBy'))
            WHERE extra->>'createdBy' IS NOT NULL;
        CREATE INDEX shopping_created_by ON shopping_items USING hash (lower(extra->>'createdBy'))
            WHERE extra->>'createdBy' IS NOT NULL;
        """;

    private const string LineHouseholdSchema = """
        CREATE TABLE line_binding_challenges (
            token_hash BYTEA PRIMARY KEY,
            household_id UUID NOT NULL,
            user_id UUID NOT NULL,
            expires_at TIMESTAMPTZ NOT NULL,
            FOREIGN KEY (household_id, user_id) REFERENCES household_members ON DELETE CASCADE
        );
        CREATE INDEX line_binding_challenge_membership ON line_binding_challenges(household_id, user_id, expires_at);

        CREATE TABLE line_bindings (
            line_user_id TEXT PRIMARY KEY,
            household_id UUID NOT NULL,
            user_id UUID NOT NULL,
            FOREIGN KEY (household_id, user_id) REFERENCES household_members ON DELETE CASCADE
        );
        CREATE INDEX line_binding_membership ON line_bindings(household_id, user_id);

        CREATE TABLE line_household_notifications (
            line_user_id TEXT NOT NULL,
            day DATE NOT NULL,
            household_id UUID NOT NULL,
            user_id UUID NOT NULL,
            message TEXT NOT NULL,
            sent BOOLEAN NOT NULL DEFAULT FALSE,
            PRIMARY KEY (line_user_id, day),
            FOREIGN KEY (household_id, user_id) REFERENCES household_members ON DELETE CASCADE
        );
        CREATE INDEX line_household_notification_membership ON line_household_notifications(household_id, user_id);
        """;

    private const string LineDeliveryAndInvitationPrivacy = """
        CREATE INDEX household_invitations_email ON household_invitations (lower(email));

        ALTER TABLE line_subscriptions
            ADD COLUMN rebind_sent BOOLEAN NOT NULL DEFAULT FALSE,
            ADD COLUMN rebind_started TIMESTAMPTZ,
            ADD COLUMN rebind_retry_key UUID NOT NULL DEFAULT gen_random_uuid();

        ALTER TABLE line_household_notifications
            ALTER COLUMN line_user_id TYPE BYTEA USING sha256(convert_to(line_user_id, 'UTF8')),
            ADD COLUMN disposition TEXT NOT NULL DEFAULT 'uncertain'
                CHECK (disposition IN ('ready', 'uncertain', 'rejected', 'sent')),
            ADD COLUMN retry_key UUID,
            ALTER COLUMN household_id DROP NOT NULL,
            ALTER COLUMN user_id DROP NOT NULL,
            DROP CONSTRAINT line_household_notifications_household_id_user_id_fkey,
            ADD FOREIGN KEY (household_id, user_id) REFERENCES household_members ON DELETE SET NULL;
        ALTER TABLE line_household_notifications RENAME COLUMN line_user_id TO recipient_hash;
        UPDATE line_household_notifications SET disposition = 'sent' WHERE sent;
        ALTER TABLE line_household_notifications DROP COLUMN sent;

        CREATE FUNCTION erase_detached_line_notification() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW.household_id IS NULL THEN
                NEW.message = '';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER erase_detached_line_notification
            BEFORE UPDATE OF household_id, user_id ON line_household_notifications
            FOR EACH ROW EXECUTE FUNCTION erase_detached_line_notification();
        """;
}
