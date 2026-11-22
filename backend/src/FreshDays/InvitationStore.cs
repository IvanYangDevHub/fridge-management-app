using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;

namespace FreshDays;

internal sealed record PendingInvitation(Guid Id, string Email, DateTimeOffset ExpiresAt);
internal sealed record CreatedInvitation(Guid Id, string Email, DateTimeOffset ExpiresAt, string Token);

internal sealed class InvitationStore(NpgsqlDataSource source, TimeProvider clock)
{
    // Base64url encoding of 32 random bytes without padding.
    public const int TokenLength = 43;

    public async Task<CreatedInvitation> Create(NpgsqlTransaction transaction, Guid householdId, string email,
        CancellationToken ct)
    {
        var invitation = new CreatedInvitation(Guid.NewGuid(), email, clock.GetUtcNow().AddDays(7),
            WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)));
        await using var command = new NpgsqlCommand("""
            INSERT INTO household_invitations (id, household_id, email, token_hash, expires_at)
            VALUES (@id, @household, @email, @hash, @expires);
            """, transaction.Connection, transaction);
        command.Parameters.AddWithValue("id", invitation.Id);
        command.Parameters.AddWithValue("household", householdId);
        command.Parameters.AddWithValue("email", invitation.Email);
        command.Parameters.AddWithValue("hash", Hash(invitation.Token));
        command.Parameters.AddWithValue("expires", invitation.ExpiresAt);
        await command.ExecuteNonQueryAsync(ct);
        return invitation;
    }

    public async Task<IReadOnlyList<PendingInvitation>> Pending(NpgsqlTransaction transaction, Guid householdId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT id, email, expires_at FROM household_invitations
            WHERE household_id = @household AND expires_at > @now AND redeemed_at IS NULL AND revoked_at IS NULL
            ORDER BY expires_at, id;
            """, transaction.Connection, transaction);
        command.Parameters.AddWithValue("household", householdId);
        command.Parameters.AddWithValue("now", clock.GetUtcNow());
        var invitations = new List<PendingInvitation>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            invitations.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        return invitations;
    }

    public async Task<bool> Revoke(NpgsqlTransaction transaction, Guid householdId, Guid invitationId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE household_invitations SET revoked_at = @now
            WHERE household_id = @household AND id = @invitation
                AND expires_at > @now AND redeemed_at IS NULL AND revoked_at IS NULL;
            """, transaction.Connection, transaction);
        command.Parameters.AddWithValue("household", householdId);
        command.Parameters.AddWithValue("invitation", invitationId);
        command.Parameters.AddWithValue("now", clock.GetUtcNow());
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<Household?> Redeem(string token, Guid userId, CancellationToken ct)
    {
        var hash = Hash(token);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Acquire the household lock before the invitation lock, matching household deletion.
        var household = await FindHousehold(transaction, hash, ct);
        if (household is null)
            return null;

        // Wait for any concurrent redeemer or revocation before checking the expiration.
        // The user lock keeps the current Google email stable until membership is committed.
        await using var find = new NpgsqlCommand("""
            SELECT invitation.id
            FROM household_invitations AS invitation
            JOIN users AS invitee ON invitee.id = @user AND invitee.email = invitation.email
            WHERE invitation.token_hash = @hash AND invitee.google_email_authoritative
                AND invitation.redeemed_at IS NULL AND invitation.revoked_at IS NULL
            FOR UPDATE OF invitation FOR SHARE OF invitee;
            """, connection, transaction);
        find.Parameters.AddWithValue("hash", hash);
        find.Parameters.AddWithValue("user", userId);
        if (await find.ExecuteScalarAsync(ct) is not Guid invitationId)
            return null;

        await using var admit = new NpgsqlCommand("""
            WITH redeemed AS (
                UPDATE household_invitations SET redeemed_at = @now
                WHERE id = @invitation AND expires_at > @now
                RETURNING household_id
            )
            INSERT INTO household_members (household_id, user_id, role)
            SELECT household_id, @user, 'member' FROM redeemed
            ON CONFLICT (household_id, user_id) DO NOTHING;
            """, connection, transaction);
        admit.Parameters.AddWithValue("invitation", invitationId);
        admit.Parameters.AddWithValue("user", userId);
        admit.Parameters.AddWithValue("now", clock.GetUtcNow());
        if (await admit.ExecuteNonQueryAsync(ct) != 1)
            return null;

        await transaction.CommitAsync(ct);
        return household;
    }

    private static async Task<Household?> FindHousehold(NpgsqlTransaction transaction, byte[] hash, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT household.id, household.name FROM household_invitations AS invitation
            JOIN households AS household ON household.id = invitation.household_id
            WHERE invitation.token_hash = @hash
            FOR KEY SHARE OF household;
            """, transaction.Connection, transaction);
        command.Parameters.AddWithValue("hash", hash);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetGuid(0), reader.GetString(1), "member") : null;
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
