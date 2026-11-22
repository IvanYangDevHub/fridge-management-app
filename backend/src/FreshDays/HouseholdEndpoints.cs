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

        routes.MapHouseholdMembership();
        routes.MapDelete("/{householdId:guid}", (Guid householdId, bool? confirm, HttpContext context, NpgsqlDataSource source) =>
            MembershipEndpoints.ChangeMembership(householdId, context, source, async (transaction, _, role, ct) =>
            {
                if (role != "owner")
                    return Results.Forbid();
                if (confirm != true)
                    return Results.BadRequest();

                using var command = new NpgsqlCommand("DELETE FROM households WHERE id = @household",
                    transaction.Connection, transaction);
                command.Parameters.AddWithValue("household", householdId);
                await command.ExecuteNonQueryAsync(ct);
                return Results.NoContent();
            }));

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

        routes.MapPost("/{householdId:guid}/line-binding", async (Guid householdId, HttpContext context, LineHouseholdStore store) =>
        {
            var token = await store.CreateChallenge(householdId, AppSessions.UserId(context.User), context.RequestAborted);
            return token is null ? Results.Forbid() : Results.Ok(new { command = $"綁定 {token}" });
        });

        routes.MapInventory();
        routes.MapShopping();
    }

    private sealed record HouseholdName(string? Name);
}
