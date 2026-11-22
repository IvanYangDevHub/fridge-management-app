using Npgsql;

namespace FreshDays;

internal static class AccountDeletion
{
    public static async Task<bool> Delete(NpgsqlDataSource source, Guid userId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        bool? deleted;
        do
        {
            deleted = await TryDelete(connection, userId, ct);
        } while (!deleted.HasValue);

        return deleted.Value;
    }

    private static async Task<bool?> TryDelete(NpgsqlConnection connection, Guid userId, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var locked = await LockHouseholds(transaction, userId, ct);

        // Invitation redemption locks its household before its user, so lock every affected
        // household in UUID order before the user. A new membership may have committed while
        // waiting for the user lock. Roll back and include it in the next ordered lock pass.
        await using var user = Command(transaction, "SELECT id FROM users WHERE id = @user FOR UPDATE", userId);
        await user.ExecuteScalarAsync(ct);

        // Invitation creation also takes this lock after its household lock. An invitation
        // that commits before this lock is acquired must be included in the next lock pass.
        await using var email = Command(transaction, """
            SELECT lower(email) FROM users WHERE id = @user;
            """, userId);
        if (await email.ExecuteScalarAsync(ct) is string address)
            await InvitationStore.LockEmail(transaction, address, ct);

        await using var check = Command(transaction, $"""
            SELECT EXISTS (
                SELECT 1 FROM ({AffectedHouseholds}) AS affected WHERE NOT (household_id = ANY (@locked))
            );
            """, userId);
        check.Parameters.AddWithValue("locked", locked);
        if ((bool)(await check.ExecuteScalarAsync(ct))!)
            return null;

        await using var owns = Command(transaction, """
            SELECT EXISTS (SELECT 1 FROM household_members WHERE user_id = @user AND role = 'owner');
            """, userId);
        if ((bool)(await owns.ExecuteScalarAsync(ct))!)
            return false;

        await using var erase = Command(transaction, $"""
            UPDATE inventory_items SET extra = extra - 'createdBy', version = version + 1
            WHERE household_id = ANY (@locked) AND {MatchesAttribution};
            UPDATE shopping_items SET extra = extra - 'createdBy', version = version + 1
            WHERE household_id = ANY (@locked) AND {MatchesAttribution};
            DELETE FROM household_invitations WHERE household_id = ANY (@locked) AND {MatchesInvitation};
            DELETE FROM users WHERE id = @user;
            """, userId);
        erase.Parameters.AddWithValue("locked", locked);
        await erase.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static async Task<Guid[]> LockHouseholds(NpgsqlTransaction transaction, Guid userId, CancellationToken ct)
    {
        await using var command = Command(transaction, $"""
            SELECT id FROM households WHERE id IN ({AffectedHouseholds}) ORDER BY id FOR UPDATE;
            """, userId);
        var households = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            households.Add(reader.GetGuid(0));

        return households.ToArray();
    }

    private static NpgsqlCommand Command(NpgsqlTransaction transaction, string sql, Guid userId)
    {
        var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue("user", userId);
        return command;
    }

    private const string MatchesAttribution = """
        extra->>'createdBy' IS NOT NULL
        AND (lower(extra->>'createdBy') = @user::text
             OR lower(extra->>'createdBy') = (SELECT lower(email) FROM users WHERE id = @user))
        """;

    private const string MatchesInvitation = "lower(email) = (SELECT lower(email) FROM users WHERE id = @user)";

    private const string AffectedHouseholds = $"""
        SELECT household_id FROM household_members WHERE user_id = @user
        UNION ALL
        SELECT household_id FROM inventory_items WHERE {MatchesAttribution}
        UNION ALL
        SELECT household_id FROM shopping_items WHERE {MatchesAttribution}
        UNION ALL
        SELECT household_id FROM household_invitations WHERE {MatchesInvitation}
        """;
}
