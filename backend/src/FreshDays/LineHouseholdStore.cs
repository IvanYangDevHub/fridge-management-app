using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace FreshDays;

public sealed class LineHouseholdStore(NpgsqlDataSource source, TimeProvider clock)
{
    internal async Task<string?> CreateChallenge(Guid householdId, Guid userId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (!await HouseholdStore.LockMembership(transaction, householdId, userId, ct))
            return null;

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var command = new NpgsqlCommand("""
            DELETE FROM line_binding_challenges
            WHERE household_id = @household AND user_id = @user AND expires_at <= @now;
            INSERT INTO line_binding_challenges (token_hash, household_id, user_id, expires_at)
            VALUES (@hash, @household, @user, @expires);
            """, connection, transaction);
        var now = clock.GetUtcNow();
        command.Parameters.AddWithValue("household", householdId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("hash", Hash(token));
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expires", now.AddMinutes(10));
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return token;
    }

    internal async Task<bool> Bind(string lineUserId, string token, CancellationToken ct)
    {
        if (token.Length != 48)
            return false;

        // Lock the household before taking the challenge row. Membership removal
        // takes the same household lock and cascades to both challenge and binding.
        await using var command = source.CreateCommand("""
            WITH allowed AS MATERIALIZED (
                SELECT challenge.token_hash
                FROM line_binding_challenges AS challenge
                JOIN households ON households.id = challenge.household_id
                WHERE challenge.token_hash = @hash
                FOR KEY SHARE OF households
            ), consumed AS (
                DELETE FROM line_binding_challenges AS challenge
                USING allowed
                WHERE challenge.token_hash = allowed.token_hash AND challenge.expires_at > @now
                RETURNING challenge.household_id, challenge.user_id
            )
            INSERT INTO line_bindings (line_user_id, household_id, user_id)
            SELECT @line, household_id, user_id FROM consumed
            ON CONFLICT (line_user_id) DO UPDATE
            SET household_id = EXCLUDED.household_id, user_id = EXCLUDED.user_id;
            """);
        command.Parameters.AddWithValue("line", lineUserId);
        command.Parameters.AddWithValue("hash", Hash(token));
        command.Parameters.AddWithValue("now", clock.GetUtcNow());
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    internal async Task<LineBinding?> Find(string lineUserId, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            SELECT household_id, user_id FROM line_bindings WHERE line_user_id = @line;
            """);
        command.Parameters.AddWithValue("line", lineUserId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(lineUserId, reader.GetGuid(0), reader.GetGuid(1)) : null;
    }

    internal async Task<bool> UseBinding(LineBinding binding, Func<NpgsqlTransaction, Task> action, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (!await HouseholdStore.LockMembership(transaction, binding.HouseholdId, binding.UserId, ct))
            return false;

        // Removal and deletion lock the household before the membership. Recheck the
        // binding after both locks so a rebind cannot send a stale household's summary.
        await using var command = BindingCommand("""
            SELECT 1 FROM line_bindings
            WHERE line_user_id = @line AND household_id = @household AND user_id = @user FOR UPDATE;
            """, transaction, binding);
        if (await command.ExecuteScalarAsync(ct) is null)
            return false;

        await action(transaction);
        await transaction.CommitAsync(ct);
        return true;
    }

    internal async Task<IReadOnlyList<LineBinding>> ReminderCandidates(DateOnly day, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            SELECT binding.line_user_id, binding.household_id, binding.user_id
            FROM line_bindings AS binding
            LEFT JOIN line_household_notifications AS reminder
                ON reminder.line_user_id = binding.line_user_id AND reminder.day = @day
            WHERE reminder.day IS NULL
                OR (NOT reminder.sent AND reminder.household_id = binding.household_id AND reminder.user_id = binding.user_id)
            ORDER BY binding.line_user_id;
            """);
        command.Parameters.AddWithValue("day", day);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var candidates = new List<LineBinding>();
        while (await reader.ReadAsync(ct))
            candidates.Add(new(reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2)));
        return candidates;
    }

    internal static async Task QueueReminder(NpgsqlTransaction transaction, LineBinding binding, DateOnly day,
        string message, CancellationToken ct)
    {
        await using var command = ReminderCommand("""
            INSERT INTO line_household_notifications (line_user_id, day, household_id, user_id, message)
            VALUES (@line, @day, @household, @user, @message) ON CONFLICT DO NOTHING;
            """, transaction, binding, day);
        command.Parameters.AddWithValue("message", message);
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static async Task<string?> PendingReminder(NpgsqlTransaction transaction, LineBinding binding, DateOnly day,
        CancellationToken ct)
    {
        await using var command = ReminderCommand("""
            SELECT message FROM line_household_notifications
            WHERE line_user_id = @line AND day = @day AND household_id = @household AND user_id = @user AND NOT sent;
            """, transaction, binding, day);
        return (string?)await command.ExecuteScalarAsync(ct);
    }

    internal static async Task MarkReminderSent(NpgsqlTransaction transaction, LineBinding binding, DateOnly day,
        CancellationToken ct)
    {
        await using var command = ReminderCommand("""
            UPDATE line_household_notifications SET sent = TRUE, message = ''
            WHERE line_user_id = @line AND day = @day;
            """, transaction, binding, day);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlCommand ReminderCommand(string sql, NpgsqlTransaction transaction, LineBinding binding, DateOnly day)
    {
        var command = BindingCommand(sql, transaction, binding);
        command.Parameters.AddWithValue("day", day);
        return command;
    }

    internal async Task Unbind(string lineUserId, CancellationToken ct)
    {
        await using var command = source.CreateCommand("DELETE FROM line_bindings WHERE line_user_id = @line;");
        command.Parameters.AddWithValue("line", lineUserId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlCommand BindingCommand(string sql, NpgsqlTransaction transaction, LineBinding binding)
    {
        var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue("line", binding.LineUserId);
        command.Parameters.AddWithValue("household", binding.HouseholdId);
        command.Parameters.AddWithValue("user", binding.UserId);
        return command;
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}

internal sealed record LineBinding(string LineUserId, Guid HouseholdId, Guid UserId);
