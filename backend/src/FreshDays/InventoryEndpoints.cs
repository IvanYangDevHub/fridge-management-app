using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace FreshDays;

internal static class InventoryEndpoints
{
    public static void MapInventory(this RouteGroupBuilder routes)
    {
        var inventory = routes.MapGroup("/{householdId:guid}/inventory");
        inventory.MapGet("", (HttpContext context, Guid householdId, NpgsqlDataSource source) =>
            WithMembership(context, householdId, source, async store =>
                Results.Text(await store.Read(), "application/json")));
        inventory.MapDelete("/item", (HttpContext context, Guid householdId, NpgsqlDataSource source, string id, long version) =>
            WithMembership(context, householdId, source, async store =>
            {
                if (!ValidId(id) || version <= 0)
                    return Results.BadRequest();

                return await store.Delete(id, version) ? Results.NoContent() : Results.Conflict();
            }));
        inventory.MapPut("/item", [RequestSizeLimit(1_048_576)] (HttpContext context, Guid householdId, NpgsqlDataSource source, string id) =>
            Update(context, householdId, source, id));
        inventory.MapPost("", [RequestSizeLimit(1_048_576)] (HttpContext context, Guid householdId, NpgsqlDataSource source) =>
            WithMembership(context, householdId, source, async store =>
            {
                var payload = await ReadPayload(context, store);
                if (payload.Error is not null)
                    return payload.Error;

                var item = await store.Create(payload.Body);
                return Results.Text(item, "application/json", statusCode: StatusCodes.Status201Created);
            }));
    }

    private static Task<IResult> Update(HttpContext context, Guid householdId, NpgsqlDataSource source, string id) =>
        WithMembership(context, householdId, source, async store =>
        {
            if (!ValidId(id))
                return Results.BadRequest();

            var payload = await ReadPayload(context, store);
            if (payload.Error is not null)
                return payload.Error;

            if (!payload.Body.TryGetProperty("version", out var value) || value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt64(out var version) || version <= 0)
                return Results.BadRequest();

            var item = await store.Update(id, version, payload.Body);
            return item is null ? Results.Conflict() : Results.Text(item, "application/json");
        });

    private static bool ValidId(string id) => !string.IsNullOrWhiteSpace(id) && !id.Contains('\0');

    private static async Task<IResult> WithMembership(HttpContext context, Guid householdId, NpgsqlDataSource source,
        Func<InventoryStore, Task<IResult>> handle)
    {
        var cancellationToken = context.RequestAborted;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await HouseholdStore.LockMembership(transaction, householdId, AppSessions.UserId(context.User), cancellationToken))
            return Results.Forbid();

        var result = await handle(new InventoryStore(transaction, householdId, cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<(JsonElement Body, IResult? Error)> ReadPayload(HttpContext context, InventoryStore store)
    {
        if (context.Request.ContentLength > 1_048_576)
            return (default, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));

        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var item = document.RootElement;
            if (item.ValueKind != JsonValueKind.Object ||
                !await store.ValidJson(item) ||
                !item.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(name.GetString()) ||
                name.GetString()!.Length > 100)
                return (default, Results.BadRequest());

            if (item.TryGetProperty("expiresAt", out var date) && date.ValueKind != JsonValueKind.Null &&
                (date.ValueKind != JsonValueKind.String || !DateOnly.TryParseExact(date.GetString(), "yyyy-MM-dd", out _)))
                return (default, Results.BadRequest());

            return (item.Clone(), null);
        }
        catch (JsonException)
        {
            return (default, Results.BadRequest());
        }
    }
}