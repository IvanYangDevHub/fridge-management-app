using System.Net.Mail;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace FreshDays;

internal static class InvitationEndpoints
{
    public static void MapInvitations(this WebApplication app, bool phaseTwo)
    {
        if (!phaseTwo)
            return;

        var routes = app.MapGroup("/api/households").RequireAuthorization();
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        var owner = routes.MapGroup("/{householdId:guid}/invitations");
        owner.MapGet("", (Guid householdId, HttpContext context, NpgsqlDataSource source, InvitationStore store) =>
            WithOwner(householdId, context, source, async (transaction, ct) =>
                Results.Ok(await store.Pending(transaction, householdId, ct))));

        owner.MapPost("", [RequestSizeLimit(4_096)] (Guid householdId, InvitationEmail request, HttpContext context,
            NpgsqlDataSource source, InvitationStore store) =>
            WithOwner(householdId, context, source, async (transaction, ct) =>
            {
                var email = request.Email?.Trim().ToLowerInvariant() ?? "";
                if (email.Length > 254 || !MailAddress.TryCreate(email, out var address) || address.Address != email)
                    return Results.BadRequest();

                var invitation = await store.Create(transaction, householdId, email, ct);
                return Results.Created($"/api/households/{householdId}/invitations/{invitation.Id}", invitation);
            }));

        owner.MapDelete("/{invitationId:guid}", (Guid householdId, Guid invitationId, HttpContext context,
            NpgsqlDataSource source, InvitationStore store) =>
            WithOwner(householdId, context, source, async (transaction, ct) =>
                await store.Revoke(transaction, householdId, invitationId, ct) ? Results.NoContent() : Results.NotFound()));

        routes.MapPost("/invitations/redeem", [RequestSizeLimit(4_096)] async (InvitationToken request, HttpContext context,
            InvitationStore store) =>
        {
            if (request.Token is not { Length: InvitationStore.TokenLength } token)
                return Results.BadRequest();

            var household = await store.Redeem(token, AppSessions.UserId(context.User), context.RequestAborted);
            return household is null ? Results.NotFound() : Results.Created($"/api/households/{household.Id}", household);
        });
    }

    private static async Task<IResult> WithOwner(Guid householdId, HttpContext context, NpgsqlDataSource source,
        Func<NpgsqlTransaction, CancellationToken, Task<IResult>> action)
    {
        var ct = context.RequestAborted;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (!await HouseholdStore.LockMembership(transaction, householdId, AppSessions.UserId(context.User), ct, ownerOnly: true))
            return Results.Forbid();

        var result = await action(transaction, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    private sealed record InvitationEmail(string? Email);
    private sealed record InvitationToken(string? Token);
}
