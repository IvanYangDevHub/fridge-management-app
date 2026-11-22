using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace FreshDays;

internal static class AuthEndpoints
{
    public static void AddAppSessions(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AppSessions>();
        services.AddSingleton<GoogleIdentity>();
        services.AddSingleton<LegacyClaim>();
        services.AddHttpClient("GoogleIdentity");
        services.AddAuthentication(AppSessions.Scheme)
            .AddScheme<AuthenticationSchemeOptions, AppSessionHandler>(AppSessions.Scheme, _ => { });
        services.AddAuthorization();
    }

    public static void MapAppSessions(this WebApplication app, bool phaseTwo)
    {
        var routes = app.MapGroup("/api/auth");
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        if (!phaseTwo)
        {
            routes.MapGet("/config", () => Results.Ok(new { mode = "phase-one" }));
            return;
        }

        var clientId = app.Configuration["Auth:GoogleClientId"];
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Auth__GoogleClientId is required in phase two.");

        routes.MapGet("/config", () => Results.Ok(new { mode = "phase-two", googleClientId = clientId }));
        routes.MapPost("/google",
            [RequestSizeLimit(16_384)] async (GoogleCredential request, HttpContext context, GoogleIdentity google, AppSessions sessions, LegacyClaim legacy) =>
            {
                var account = await google.Verify(request.Credential);
                if (account is null)
                    return Results.Unauthorized();

                var user = await sessions.Start(account, context);
                return Results.Ok(await Describe(user, context, legacy));
            });

        routes.MapGet("/session", async (HttpContext context, LegacyClaim legacy) =>
            Results.Ok(await Describe(AppSessions.User(context.User), context, legacy)))
            .RequireAuthorization();
        routes.MapPost("/logout", async (HttpContext context, AppSessions sessions) =>
        {
            await sessions.End(context);
            return Results.NoContent();
        }).RequireAuthorization();

        routes.MapDelete("/account", async (bool? confirm, HttpContext context, NpgsqlDataSource source) =>
        {
            if (confirm != true)
                return Results.BadRequest();
            if (!await AccountDeletion.Delete(source, AppSessions.UserId(context.User), context.RequestAborted))
                return Results.Conflict();

            AppSessions.ClearCookie(context);
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapPost("/api/households/legacy/claim", async (HttpContext context, LegacyClaim legacy) =>
        {
            var result = await legacy.Claim(AppSessions.UserId(context.User), context.RequestAborted);
            if (!result.Authorized)
                return Results.Forbid();
            if (!result.Claimed)
                return Results.Conflict();

            var householdId = PostgresStore.LegacyHouseholdId;
            return Results.Created($"/api/households/{householdId}", new { householdId });
        }).RequireAuthorization();
    }

    private static async Task<object> Describe(SessionUser user, HttpContext context, LegacyClaim legacy) =>
        new { user.UserId, user.Email, CanClaimLegacy = await legacy.IsAvailable(user.UserId, context.RequestAborted) };

    private sealed record GoogleCredential(string? Credential);
}
