using System.Text.Json;
using Npgsql;

namespace FreshDays;

internal sealed class InventoryStore(NpgsqlTransaction transaction, Guid householdId, CancellationToken cancellationToken)
{
    private const string ItemJson = """
        extra || jsonb_build_object('id', id, 'name', name, 'version', version) ||
        CASE WHEN expires_at IS NULL THEN '{}'::jsonb ELSE jsonb_build_object('expiresAt', expires_at) END
        """;

    public async Task<string> Read()
    {
        await using var command = Command($"""
            SELECT COALESCE(jsonb_agg({ItemJson} ORDER BY position, extra->>'createdAt' DESC NULLS LAST, id),
                            '[]'::jsonb)::text
            FROM inventory_items
            WHERE household_id = @household;
            """);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<bool> ValidJson(JsonElement item)
    {
        await using var command = new NpgsqlCommand("SELECT pg_input_is_valid(@item, 'jsonb')", transaction.Connection, transaction);
        command.Parameters.AddWithValue("item", item.GetRawText());
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<string> Create(JsonElement item)
    {
        await using var command = Command($"""
            WITH input AS (SELECT @item::jsonb AS data)
            INSERT INTO inventory_items (household_id, id, name, position, expires_at, extra)
            SELECT @household, @id, data->>'name', -1, data->'expiresAt',
                   data - 'id' - 'name' - 'expiresAt' - 'version'
            FROM input
            RETURNING ({ItemJson})::text;
            """);
        command.Parameters.AddWithValue("id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("item", item.GetRawText());
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<string?> Update(string id, long version, JsonElement item)
    {
        await using var command = Command($"""
            WITH input AS (SELECT @item::jsonb AS data)
            UPDATE inventory_items
            SET name = data->>'name',
                expires_at = COALESCE(data->'expiresAt', expires_at),
                extra = extra || (data - 'id' - 'name' - 'expiresAt' - 'version'),
                version = version + 1
            FROM input
            WHERE household_id = @household AND id = @id AND version = @version
            RETURNING ({ItemJson})::text;
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("item", item.GetRawText());
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<bool> Delete(string id, long version)
    {
        await using var command = Command("""
            DELETE FROM inventory_items WHERE household_id = @household AND id = @id AND version = @version;
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("version", version);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private NpgsqlCommand Command(string sql)
    {
        var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue("household", householdId);
        return command;
    }
}