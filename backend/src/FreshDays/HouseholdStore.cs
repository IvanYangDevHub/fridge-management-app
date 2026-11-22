using Npgsql;

namespace FreshDays;

internal sealed record Household(Guid Id, string Name, string Role);

internal sealed class HouseholdStore(NpgsqlDataSource source)
{
    public async Task<Household> Create(Guid userId, string name, CancellationToken cancellationToken)
    {
        var household = new Household(Guid.NewGuid(), name, "owner");
        await using var command = source.CreateCommand("""
            WITH household AS (
                INSERT INTO households (id, name) VALUES (@household, @name) RETURNING id
            )
            INSERT INTO household_members (household_id, user_id, role)
            SELECT id, @user, @role FROM household;
            """);
        command.Parameters.AddWithValue("household", household.Id);
        command.Parameters.AddWithValue("name", household.Name);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("role", household.Role);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return household;
    }

    public static async Task<bool> LockMembership(NpgsqlTransaction transaction, Guid householdId, Guid userId,
        CancellationToken cancellationToken, bool ownerOnly = false)
    {
        // Lock the household before membership so deletion cannot take those locks in reverse order.
        await using var household = new NpgsqlCommand("SELECT 1 FROM households WHERE id = @household FOR KEY SHARE",
            transaction.Connection, transaction);
        household.Parameters.AddWithValue("household", householdId);
        if (await household.ExecuteScalarAsync(cancellationToken) is null)
            return false;

        await using var member = new NpgsqlCommand("""
            SELECT 1 FROM household_members
            WHERE household_id = @household AND user_id = @user AND (NOT @owner_only OR role = 'owner') FOR SHARE;
            """, transaction.Connection, transaction);
        member.Parameters.AddWithValue("household", householdId);
        member.Parameters.AddWithValue("user", userId);
        member.Parameters.AddWithValue("owner_only", ownerOnly);
        return await member.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<IReadOnlyList<Household>> List(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand("""
            SELECT households.id, households.name, household_members.role
            FROM household_members JOIN households ON households.id = household_members.household_id
            WHERE household_members.user_id = @user
            ORDER BY households.name, households.id;
            """);
        command.Parameters.AddWithValue("user", userId);
        var households = new List<Household>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            households.Add(new Household(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));

        return households;
    }
}
