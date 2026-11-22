using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FreshDays;

internal sealed record SessionUser(Guid UserId, string Email);

internal sealed class AppSessions(NpgsqlDataSource source, TimeProvider clock)
{
    public const string Scheme = "FreshDaysSession";
    public const string CookieName = "__Host-freshdays-session";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);

    public static Guid UserId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static SessionUser User(ClaimsPrincipal principal) =>
        new(UserId(principal), principal.FindFirstValue(ClaimTypes.Email)!);

    public async Task<SessionUser> Start(GoogleAccount account, HttpContext context)
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow();
        await using var command = source.CreateCommand("""
            WITH account AS (
                INSERT INTO users (id, google_sub, email, google_email_authoritative)
                VALUES (@id, @sub, @email, @authoritative)
                ON CONFLICT (google_sub) DO UPDATE
                SET email = EXCLUDED.email, google_email_authoritative = EXCLUDED.google_email_authoritative
                RETURNING id
            ), expired AS (
                DELETE FROM app_sessions WHERE expires_at <= @now
            )
            INSERT INTO app_sessions (token_hash, user_id, expires_at)
            SELECT @hash, id, @expires FROM account
            RETURNING user_id;
            """);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("sub", account.Subject);
        command.Parameters.AddWithValue("email", account.Email);
        command.Parameters.AddWithValue("authoritative", account.EmailAuthoritative);
        command.Parameters.AddWithValue("hash", Hash(token));
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expires", now + SessionLifetime);
        var id = (Guid)(await command.ExecuteScalarAsync(context.RequestAborted))!;

        var options = CookieOptions();
        options.MaxAge = SessionLifetime;
        context.Response.Cookies.Append(CookieName, token, options);
        return new SessionUser(id, account.Email);
    }

    public async Task<SessionUser?> Read(string token, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand("""
            SELECT users.id, users.email
            FROM app_sessions JOIN users ON users.id = app_sessions.user_id
            WHERE token_hash = @hash AND expires_at > @now;
            """);
        command.Parameters.AddWithValue("hash", Hash(token));
        command.Parameters.AddWithValue("now", clock.GetUtcNow());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SessionUser(reader.GetGuid(0), reader.GetString(1))
            : null;
    }

    public async Task End(HttpContext context)
    {
        await using var command = source.CreateCommand("DELETE FROM app_sessions WHERE token_hash = @hash");
        command.Parameters.AddWithValue("hash", Hash(context.Request.Cookies[CookieName]!));
        await command.ExecuteNonQueryAsync(context.RequestAborted);
        ClearCookie(context);
    }

    public static void ClearCookie(HttpContext context) =>
        context.Response.Cookies.Delete(CookieName, CookieOptions());

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static CookieOptions CookieOptions() =>
        new() { Path = "/", Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax };
}

internal sealed class AppSessionHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AppSessions sessions) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(AppSessions.CookieName, out var token))
            return AuthenticateResult.NoResult();

        if (!HttpMethods.IsGet(Request.Method) &&
            Request.Headers.TryGetValue("Sec-Fetch-Site", out var site) && site != "same-origin")
            return AuthenticateResult.Fail("Cross-origin session request.");

        var user = await sessions.Read(token, Context.RequestAborted);
        if (user is null)
            return AuthenticateResult.Fail("Invalid or expired session.");

        var identity = new ClaimsIdentity(
            [new(ClaimTypes.NameIdentifier, user.UserId.ToString()), new(ClaimTypes.Email, user.Email)],
            Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
