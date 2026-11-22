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
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Account deletion and invitation redemption acquire the household before the user.
        await using var household = new NpgsqlCommand("SELECT id FROM households WHERE id = @household FOR KEY SHARE",
            connection, transaction);
        household.Parameters.AddWithValue("household", PostgresStore.LegacyHouseholdId);
        var householdExists = await household.ExecuteScalarAsync(cancellationToken) is not null;

        await using var command = new NpgsqlCommand("""
            WITH authorized AS (
                SELECT id FROM users
                WHERE id = @user AND email = @email AND google_email_authoritative
                FOR NO KEY UPDATE
            ), claimed AS (
                UPDATE legacy_household_claim SET claimed = TRUE
                WHERE @household_exists AND NOT claimed AND EXISTS (SELECT 1 FROM authorized)
                RETURNING singleton
            ), membership AS (
                INSERT INTO household_members (household_id, user_id, role)
                SELECT @household, @user, 'owner' FROM claimed
                RETURNING household_id
            )
            SELECT EXISTS (SELECT 1 FROM authorized), EXISTS (SELECT 1 FROM membership);
            """, connection, transaction);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("email", ownerEmail);
        command.Parameters.AddWithValue("household", PostgresStore.LegacyHouseholdId);
        command.Parameters.AddWithValue("household_exists", householdExists);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var result = new LegacyClaimResult(reader.GetBoolean(0), reader.GetBoolean(1));
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
