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

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockLine(transaction, lineUserId, ct);

        // Lock the household before taking the challenge row. Membership removal
        // takes the same household lock and cascades to both challenge and binding.
        await using var command = new NpgsqlCommand("""
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
            """, connection, transaction);
        command.Parameters.AddWithValue("line", lineUserId);
        command.Parameters.AddWithValue("hash", Hash(token));
        command.Parameters.AddWithValue("now", clock.GetUtcNow());
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            return false;

        await using var retire = new NpgsqlCommand("""
            UPDATE line_subscriptions SET rebind_sent = TRUE WHERE household_id = @legacy AND user_id = @line;
            """, connection, transaction);
        retire.Parameters.AddWithValue("legacy", PostgresStore.LegacyHouseholdId);
        retire.Parameters.AddWithValue("line", lineUserId);
        await retire.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
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

    internal async Task WithLineLock(string lineUserId, Func<Task> action, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockLine(transaction, lineUserId, ct);
        await action();
        await transaction.CommitAsync(ct);
    }

    private static async Task LockLine(NpgsqlTransaction transaction, string lineUserId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@line, 71423509))",
            transaction.Connection, transaction);
        command.Parameters.AddWithValue("line", lineUserId);
        await command.ExecuteNonQueryAsync(ct);
    }

    internal async Task<IReadOnlyList<string>> LegacyNoticeCandidates(DateTimeOffset now, CancellationToken ct)
    {
        await using var command = source.CreateCommand($"""
            SELECT user_id FROM line_subscriptions WHERE {PendingLegacyNotice} ORDER BY user_id;
            """);
        SetNoticeParameters(command, now);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var candidates = new List<string>();
        while (await reader.ReadAsync(ct))
            candidates.Add(reader.GetString(0));
        return candidates;
    }

    internal async Task<Guid?> BeginLegacyNotice(string lineUserId, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = source.CreateCommand($"""
            UPDATE line_subscriptions SET rebind_started = COALESCE(rebind_started, @now)
            WHERE user_id = @line AND {PendingLegacyNotice}
            RETURNING rebind_retry_key;
            """);
        SetNoticeParameters(command, now);
        command.Parameters.AddWithValue("line", lineUserId);
        return (Guid?)await command.ExecuteScalarAsync(ct);
    }

    internal async Task RetireLegacyNotice(string lineUserId, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            UPDATE line_subscriptions SET rebind_sent = TRUE WHERE household_id = @legacy AND user_id = @line;
            """);
        command.Parameters.AddWithValue("legacy", PostgresStore.LegacyHouseholdId);
        command.Parameters.AddWithValue("line", lineUserId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private const string PendingLegacyNotice = """
        household_id = @legacy AND NOT rebind_sent
            AND (rebind_started IS NULL OR @now < rebind_started + INTERVAL '24 hours')
            AND NOT EXISTS (SELECT 1 FROM line_bindings WHERE line_user_id = line_subscriptions.user_id)
        """;

    private static void SetNoticeParameters(NpgsqlCommand command, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("legacy", PostgresStore.LegacyHouseholdId);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
    }

    internal async Task<IReadOnlyList<LineBinding>> ReminderCandidates(DateOnly day, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            DELETE FROM line_household_notifications WHERE day < @day;
            SELECT binding.line_user_id, binding.household_id, binding.user_id
            FROM line_bindings AS binding
            LEFT JOIN line_household_notifications AS reminder
                ON reminder.recipient_hash = sha256(convert_to(binding.line_user_id, 'UTF8')) AND reminder.day = @day
            WHERE reminder.day IS NULL OR reminder.disposition = 'ready'
                OR (reminder.disposition = 'uncertain' AND
                    (reminder.household_id, reminder.user_id) = (binding.household_id, binding.user_id))
                OR (reminder.disposition = 'rejected' AND
                    (reminder.household_id, reminder.user_id) IS DISTINCT FROM (binding.household_id, binding.user_id))
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
            INSERT INTO line_household_notifications (recipient_hash, day, household_id, user_id, message, disposition)
            VALUES (sha256(convert_to(@line, 'UTF8')), @day, @household, @user, @message, 'ready')
            ON CONFLICT (recipient_hash, day) DO UPDATE
            SET household_id = EXCLUDED.household_id, user_id = EXCLUDED.user_id, message = EXCLUDED.message,
                retry_key = gen_random_uuid(), disposition = 'ready'
            WHERE line_household_notifications.disposition IN ('ready', 'rejected')
                AND (line_household_notifications.household_id, line_household_notifications.user_id)
                    IS DISTINCT FROM (@household, @user);
            """, transaction, binding, day);
        command.Parameters.AddWithValue("message", message);
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static async Task<LineReminder?> BeginReminder(NpgsqlTransaction transaction, LineBinding binding,
        DateOnly day, CancellationToken ct)
    {
        // Persist the uncertainty before issuing the push. A process can stop after LINE
        // accepted the request, so a fresh binding must never replace an uncertain request.
        await using var command = ReminderCommand("""
            WITH pending AS MATERIALIZED (
                SELECT recipient_hash, day, message, retry_key, disposition
                FROM line_household_notifications
                WHERE recipient_hash = sha256(convert_to(@line, 'UTF8')) AND day = @day
                    AND household_id = @household AND user_id = @user AND disposition IN ('ready', 'uncertain')
            )
            UPDATE line_household_notifications AS notification SET disposition = 'uncertain'
            FROM pending WHERE notification.recipient_hash = pending.recipient_hash AND notification.day = pending.day
            RETURNING pending.message, pending.retry_key, pending.disposition = 'ready';
            """, transaction, binding, day);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        // Null is the original stable request key for this recipient and date, including requests from version 5.
        var key = reader.IsDBNull(1) ? DailyKey(binding.LineUserId, day) : reader.GetGuid(1);
        return new LineReminder(reader.GetString(0), key, reader.GetBoolean(2));
    }

    private static Guid DailyKey(string lineUserId, DateOnly day)
    {
        var identity = FormattableString.Invariant($"freshdays-household:{day.DayNumber}:{lineUserId}");
        return new Guid(Hash(identity).AsSpan(0, 16));
    }

    internal static async Task FinishReminder(NpgsqlTransaction transaction, LineBinding binding, DateOnly day,
        string disposition, CancellationToken ct)
    {
        await using var command = ReminderCommand("""
            UPDATE line_household_notifications SET disposition = @disposition, message = ''
            WHERE recipient_hash = sha256(convert_to(@line, 'UTF8')) AND day = @day;
            """, transaction, binding, day);
        command.Parameters.AddWithValue("disposition", disposition);
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
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockLine(transaction, lineUserId, ct);
        await using var command = new NpgsqlCommand("""
            DELETE FROM line_bindings WHERE line_user_id = @line;
            DELETE FROM line_subscriptions WHERE household_id = @legacy AND user_id = @line;
            """, connection, transaction);
        command.Parameters.AddWithValue("line", lineUserId);
        command.Parameters.AddWithValue("legacy", PostgresStore.LegacyHouseholdId);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
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

internal sealed record LineReminder(string Message, Guid RetryKey, bool CanReplace);
