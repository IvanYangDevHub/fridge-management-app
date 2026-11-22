using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace FreshDays;

internal static class HouseholdEndpoints
{
    public static void MapHouseholds(this WebApplication app, bool phaseTwo)
    {
        if (!phaseTwo)
            return;

        var routes = app.MapGroup("/api/households").RequireAuthorization();
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        routes.MapGet("", async (HttpContext context, HouseholdStore store) =>
            Results.Ok(await store.List(AppSessions.UserId(context.User), context.RequestAborted)));

        routes.MapPost("", [RequestSizeLimit(4_096)] async (HouseholdName request, HttpContext context, HouseholdStore store) =>
        {
            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 100 || name.Contains('\0'))
                return Results.BadRequest();

            var household = await store.Create(AppSessions.UserId(context.User), name, context.RequestAborted);
            return Results.Created($"/api/households/{household.Id}", household);
        });

        routes.MapInventory();
        routes.MapMethods("/{householdId:guid}/shopping", ["GET", "PUT"],
            (HttpContext context, Guid householdId, NpgsqlDataSource source) => HouseholdList(context, householdId, "shopping", source));
    }

    private static async Task<IResult> HouseholdList(HttpContext context, Guid householdId, string list, NpgsqlDataSource source)
    {
        var cancellationToken = context.RequestAborted;
        var userId = AppSessions.UserId(context.User);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await HouseholdStore.LockMembership(transaction, householdId, userId, cancellationToken))
            return Results.Forbid();

        if (HttpMethods.IsGet(context.Request.Method))
            return Results.Text(await PostgresStore.ReadList(list, householdId, connection, transaction, cancellationToken),
                "application/json");

        var result = await ListPayload.Replace(context, list,
            json => PostgresStore.ReplaceList(list, householdId, json, transaction, cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private sealed record HouseholdName(string? Name);
}
