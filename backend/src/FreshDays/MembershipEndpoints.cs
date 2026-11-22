using Npgsql;

namespace FreshDays;

internal static class MembershipEndpoints
{
    public static void MapHouseholdMembership(this RouteGroupBuilder routes)
    {
        var members = routes.MapGroup("/{householdId:guid}/members");

        members.MapGet("", (Guid householdId, HttpContext context, NpgsqlDataSource source) =>
            ListMembers(householdId, context, source));

        members.MapDelete("/me", (Guid householdId, HttpContext context, NpgsqlDataSource source) =>
            ChangeMembership(householdId, context, source, async (transaction, actor, role, ct) =>
            {
                if (role == "owner")
                    return Results.Conflict();

                await RemoveMember(transaction, householdId, actor, ct);
                return Results.NoContent();
            }));

        members.MapDelete("/{userId:guid}", (Guid householdId, Guid userId, HttpContext context, NpgsqlDataSource source) =>
            ChangeMembership(householdId, context, source, async (transaction, actor, role, ct) =>
            {
                if (role != "owner")
                    return Results.Forbid();
                if (actor == userId)
                    return Results.Conflict();

                return await RemoveMember(transaction, householdId, userId, ct) ? Results.NoContent() : Results.NotFound();
            }));

        members.MapPost("/{userId:guid}/transfer", (Guid householdId, Guid userId, HttpContext context, NpgsqlDataSource source) =>
            ChangeMembership(householdId, context, source, async (transaction, actor, role, ct) =>
            {
                if (role != "owner")
                    return Results.Forbid();
                if (actor == userId)
                    return Results.Conflict();
                if (await MemberRole(transaction, householdId, userId, ct) is null)
                    return Results.NotFound();

                await TransferOwnership(transaction, householdId, actor, userId, ct);
                return Results.NoContent();
            }));
    }

    private static async Task<IResult> ChangeMembership(Guid householdId, HttpContext context, NpgsqlDataSource source,
        Func<NpgsqlTransaction, Guid, string, CancellationToken, Task<IResult>> change)
    {
        var ct = context.RequestAborted;
        var actor = AppSessions.UserId(context.User);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Take the household lock before reading memberships, serializing ownership changes
        // with other membership operations and household-scoped data access.
        await using var household = new NpgsqlCommand("SELECT 1 FROM households WHERE id = @household FOR UPDATE",
            connection, transaction);
        household.Parameters.AddWithValue("household", householdId);
        if (await household.ExecuteScalarAsync(ct) is null)
            return Results.Forbid();

        var role = await MemberRole(transaction, householdId, actor, ct);
        if (role is null)
            return Results.Forbid();

        var result = await change(transaction, actor, role, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    private static async Task<IResult> ListMembers(Guid householdId, HttpContext context, NpgsqlDataSource source)
    {
        var ct = context.RequestAborted;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (!await HouseholdStore.LockMembership(transaction, householdId, AppSessions.UserId(context.User), ct, ownerOnly: true))
            return Results.Forbid();

        await using var command = new NpgsqlCommand("""
            SELECT member.user_id, users.email, member.role
            FROM household_members AS member JOIN users ON users.id = member.user_id
            WHERE member.household_id = @household
            ORDER BY member.role DESC, users.email, member.user_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("household", householdId);
        var members = new List<HouseholdMember>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            members.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));

        return Results.Ok(members);
    }

    private static async Task TransferOwnership(NpgsqlTransaction transaction, Guid householdId, Guid owner, Guid successor,
        CancellationToken ct)
    {
        // The partial unique index requires demoting the old owner before promoting the successor.
        // Both updates commit together while the household and memberships are locked.
        await using var command = new NpgsqlCommand("""
            UPDATE household_members SET role = 'member' WHERE household_id = @household AND user_id = @owner;
            UPDATE household_members SET role = 'owner' WHERE household_id = @household AND user_id = @successor;
            """, transaction.Connection, transaction);
        command.Parameters.AddWithValue("household", householdId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("successor", successor);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record HouseholdMember(Guid UserId, string Email, string Role);

    private static async Task<string?> MemberRole(NpgsqlTransaction transaction, Guid householdId, Guid user, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT role FROM household_members WHERE household_id = @household AND user_id = @user FOR UPDATE;
            """, transaction.Connection, transaction);
        command.Parameters.AddWithValue("household", householdId);
        command.Parameters.AddWithValue("user", user);
        return (string?)await command.ExecuteScalarAsync(ct);
    }

    private static async Task<bool> RemoveMember(NpgsqlTransaction transaction, Guid householdId, Guid user, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            DELETE FROM household_members WHERE household_id = @household AND user_id = @user AND role = 'member';
            """, transaction.Connection, transaction);
        command.Parameters.AddWithValue("household", householdId);
        command.Parameters.AddWithValue("user", user);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
}
