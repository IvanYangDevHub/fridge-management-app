using Npgsql;

namespace FreshDays;

internal static class ShoppingStore
{
    private const string ItemJson = """
        extra || jsonb_build_object('id', id, 'name', name,
            'completed', COALESCE(completed = 'true'::jsonb, false), 'version', version)
        """;

    public static async Task<string> Read(NpgsqlTransaction transaction, Guid householdId, CancellationToken cancellationToken)
    {
        await using var command = Command(transaction, householdId, $"""
            SELECT COALESCE(jsonb_agg({ItemJson} ORDER BY position, id), '[]'::jsonb)::text
            FROM shopping_items
            WHERE household_id = @household;
            """);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public static async Task<string> Add(NpgsqlTransaction transaction, Guid householdId, string id, string name,
        bool completed, CancellationToken cancellationToken)
    {
        await using var command = Command(transaction, householdId, $"""
            INSERT INTO shopping_items (household_id, id, name, position, completed)
            SELECT @household, @id, @name, COALESCE(MAX(position), 0) + 1, to_jsonb(@completed)
            FROM shopping_items WHERE household_id = @household
            RETURNING ({ItemJson})::text;
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("completed", completed);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public static async Task<string?> Update(NpgsqlTransaction transaction, Guid householdId, string id, string name,
        bool completed, long version, CancellationToken cancellationToken)
    {
        await using var command = Command(transaction, householdId, $"""
            UPDATE shopping_items
            SET name = @name, completed = to_jsonb(@completed), version = version + 1
            WHERE household_id = @household AND id = @id AND version = @version
            RETURNING ({ItemJson})::text;
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("completed", completed);
        command.Parameters.AddWithValue("version", version);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public static async Task<bool> Delete(NpgsqlTransaction transaction, Guid householdId, string id, long version,
        CancellationToken cancellationToken)
    {
        await using var command = Command(transaction, householdId, """
            DELETE FROM shopping_items WHERE household_id = @household AND id = @id AND version = @version;
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("version", version);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static NpgsqlCommand Command(NpgsqlTransaction transaction, Guid householdId, string sql)
    {
        var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue("household", householdId);
        return command;
    }
}
