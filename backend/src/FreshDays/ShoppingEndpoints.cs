using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;

namespace FreshDays;

internal static class ShoppingEndpoints
{
    public static void MapShopping(this RouteGroupBuilder households)
    {
        var routes = households.MapGroup("/{householdId:guid}/shopping");
        routes.MapGet("", Read);
        routes.MapPost("", [RequestSizeLimit(4_096)] (Guid householdId, NewShoppingItem item, HttpContext context, NpgsqlDataSource source) =>
            WithMembership(householdId, context, source, async (transaction, cancellationToken) =>
            {
                if (!ValidName(item.Name) || item.Completed is null)
                    return Results.BadRequest();

                var id = Guid.NewGuid().ToString();
                var created = await ShoppingStore.Add(transaction, householdId, id, item.Name!,
                    item.Completed.Value, cancellationToken);
                context.Response.Headers.Location = QueryHelpers.AddQueryString($"{context.Request.PathBase}/api/households/{householdId}/shopping/item", "id", id);
                return Results.Text(created, "application/json", statusCode: StatusCodes.Status201Created);
            }));

        routes.MapPut("/item", [RequestSizeLimit(4_096)] (Guid householdId, string id, ShoppingChange change, HttpContext context, NpgsqlDataSource source) =>
            WithMembership(householdId, context, source, async (transaction, cancellationToken) =>
            {
                if (!ValidId(id) || !ValidName(change.Name) || change.Completed is null || change.Version <= 0)
                    return Results.BadRequest();

                var changed = await ShoppingStore.Update(transaction, householdId, id, change.Name!,
                    change.Completed.Value, change.Version, cancellationToken);
                return changed is null ? Results.Conflict() : Results.Text(changed, "application/json");
            }));

        routes.MapDelete("/item", (Guid householdId, string id, long version, HttpContext context, NpgsqlDataSource source) =>
            WithMembership(householdId, context, source, async (transaction, cancellationToken) =>
            {
                if (!ValidId(id) || version <= 0)
                    return Results.BadRequest();

                return await ShoppingStore.Delete(transaction, householdId, id, version, cancellationToken)
                    ? Results.NoContent() : Results.Conflict();
            }));
    }

    private static Task<IResult> Read(Guid householdId, HttpContext context, NpgsqlDataSource source) =>
        WithMembership(householdId, context, source, async (transaction, cancellationToken) =>
            Results.Text(await ShoppingStore.Read(transaction, householdId, cancellationToken), "application/json"));

    private static async Task<IResult> WithMembership(Guid householdId, HttpContext context, NpgsqlDataSource source,
        Func<NpgsqlTransaction, CancellationToken, Task<IResult>> action)
    {
        var cancellationToken = context.RequestAborted;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await HouseholdStore.LockMembership(transaction, householdId, AppSessions.UserId(context.User), cancellationToken))
            return Results.Forbid();

        var result = await action(transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && !id.Contains('\0');
    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 100 && !name.Contains('\0');

    private sealed record NewShoppingItem(string? Name, bool? Completed);
    private sealed record ShoppingChange(string? Name, bool? Completed, long Version);
}
