using Npgsql;

namespace FreshDays;

public sealed class PostgresStore(NpgsqlDataSource source)
{
    public static readonly Guid LegacyHouseholdId = new("00000000-0000-0000-0000-000000000001");

    public async Task<string> ReadList(string name)
    {
        await using var connection = await source.OpenConnectionAsync();
        return await ReadList(name, LegacyHouseholdId, connection);
    }

    internal static async Task<string> ReadList(string name, Guid householdId, NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var (table, column, property) = ListColumns(name);
        await using var command = new NpgsqlCommand($"""
            SELECT COALESCE(jsonb_agg(
                extra || jsonb_build_object('id', id, 'name', name) ||
                CASE WHEN {column} IS NULL THEN jsonb_build_object()
                     ELSE jsonb_build_object('{property}', {column}) END
                ORDER BY position), '[]'::jsonb)::text
            FROM {table}
            WHERE household_id = @household;
            """, connection, transaction);
        command.Parameters.AddWithValue("household", householdId);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<bool> ReplaceList(string name, string json)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var replaced = await ReplaceList(name, LegacyHouseholdId, json, transaction);
        await transaction.CommitAsync();
        return replaced;
    }

    internal static async Task<bool> ReplaceList(string name, Guid householdId, string json,
        NpgsqlTransaction transaction, CancellationToken cancellationToken = default)
    {
        var (table, column, property) = ListColumns(name);
        var connection = transaction.Connection!;
        // PostgreSQL's JSONB input rejects values such as NUL characters.
        await using var validate = new NpgsqlCommand("SELECT pg_input_is_valid(@items, 'jsonb');", connection, transaction);
        validate.Parameters.AddWithValue("items", json);
        if (!(bool)(await validate.ExecuteScalarAsync(cancellationToken))!)
            return false;

        // Serialize whole-list writes even when the list is empty.
        await using var replace = new NpgsqlCommand($"""
            SELECT id FROM households WHERE id = @household FOR NO KEY UPDATE;
            DELETE FROM {table} WHERE household_id = @household;
            INSERT INTO {table} (household_id, id, name, position, {column}, extra)
            SELECT @household, item->>'id', item->>'name', position, item->'{property}',
                   item - 'id' - 'name' - '{property}'
            FROM jsonb_array_elements(@items::jsonb) WITH ORDINALITY AS input(item, position);
            """, connection, transaction);
        replace.Parameters.AddWithValue("household", householdId);
        replace.Parameters.AddWithValue("items", json);
        await replace.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    private static (string Table, string Column, string Property) ListColumns(string name) =>
        name == "inventory"
            ? ("inventory_items", "expires_at", "expiresAt")
            : ("shopping_items", "completed", "completed");

    public Task Subscribe(string userId) => ChangeSubscriber(userId, """
        INSERT INTO line_subscriptions (household_id, user_id)
        VALUES (@household, @user) ON CONFLICT DO NOTHING;
        """);

    public Task Unsubscribe(string userId) => ChangeSubscriber(userId, """
        DELETE FROM line_subscriptions WHERE household_id = @household AND user_id = @user;
        """);

    private async Task ChangeSubscriber(string userId, string sql)
    {
        await using var command = source.CreateCommand(sql);
        command.Parameters.AddWithValue("household", LegacyHouseholdId);
        command.Parameters.AddWithValue("user", userId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<string>> Subscribers()
    {
        await using var command = source.CreateCommand("""
            SELECT user_id FROM line_subscriptions WHERE household_id = @household ORDER BY user_id;
            """);
        command.Parameters.AddWithValue("household", LegacyHouseholdId);
        var subscribers = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            subscribers.Add(reader.GetString(0));

        return subscribers;
    }

    public async Task<(string Key, bool Sent)> Notification(DateOnly day, string userId)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO line_notifications (household_id, day, user_id, retry_key)
            VALUES (@household, @day, @user, @key)
            ON CONFLICT (household_id, day, user_id)
            DO UPDATE SET retry_key = line_notifications.retry_key
            RETURNING retry_key, sent;
            """);
        SetNotificationParameters(command, day, userId);
        command.Parameters.AddWithValue("key", Guid.NewGuid());
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetGuid(0).ToString(), reader.GetBoolean(1));
    }

    public async Task MarkSent(DateOnly day, string userId)
    {
        await using var command = source.CreateCommand("""
            UPDATE line_notifications SET sent = TRUE
            WHERE household_id = @household AND day = @day AND user_id = @user;
            """);
        SetNotificationParameters(command, day, userId);
        await command.ExecuteNonQueryAsync();
    }

    private static void SetNotificationParameters(NpgsqlCommand command, DateOnly day, string userId)
    {
        command.Parameters.AddWithValue("household", LegacyHouseholdId);
        command.Parameters.AddWithValue("day", day);
        command.Parameters.AddWithValue("user", userId);
    }
}
