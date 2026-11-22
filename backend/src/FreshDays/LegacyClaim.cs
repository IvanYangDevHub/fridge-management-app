using Npgsql;

namespace FreshDays;

internal sealed record LegacyClaimResult(bool Authorized, bool Claimed);

internal sealed class LegacyClaim(NpgsqlDataSource source, IConfiguration configuration)
{
    private readonly string ownerEmail = configuration.GetValue("Auth:LegacyOwnerEmail", "")!.Trim().ToLowerInvariant();

    public async Task<bool> IsAvailable(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand("""
            SELECT EXISTS (
                SELECT 1 FROM users CROSS JOIN legacy_household_claim
                WHERE id = @user AND email = @email AND google_email_authoritative AND NOT claimed
            );
            """);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("email", ownerEmail);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<LegacyClaimResult> Claim(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand("""
            WITH authorized AS (
                SELECT id FROM users
                WHERE id = @user AND email = @email AND google_email_authoritative
                FOR NO KEY UPDATE
            ), claimed AS (
                UPDATE legacy_household_claim SET claimed = TRUE
                WHERE NOT claimed AND EXISTS (SELECT 1 FROM authorized)
                RETURNING singleton
            ), membership AS (
                INSERT INTO household_members (household_id, user_id, role)
                SELECT @household, @user, 'owner' FROM claimed
                RETURNING household_id
            )
            SELECT EXISTS (SELECT 1 FROM authorized), EXISTS (SELECT 1 FROM membership);
            """);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("email", ownerEmail);
        command.Parameters.AddWithValue("household", PostgresStore.LegacyHouseholdId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new LegacyClaimResult(reader.GetBoolean(0), reader.GetBoolean(1));
    }
}
