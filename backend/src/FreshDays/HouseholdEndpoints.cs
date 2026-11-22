using Microsoft.AspNetCore.Mvc;

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
        routes.MapShopping();
    }

    private sealed record HouseholdName(string? Name);
}
