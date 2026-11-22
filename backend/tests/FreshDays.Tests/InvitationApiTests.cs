using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FreshDays.Tests;

public sealed class InvitationApiTests
{
    private const string RedeemPath = "/api/households/invitations/redeem";

    [Fact]
    public async Task AVerifiedInviteeCanJoinOnlyTheIntendedHousehold()
    {
        await using var app = await TestApp.Start();
        using var owner = await app.SignIn("owner", "owner@gmail.com");
        using var invitee = await app.SignIn("invitee", "member@gmail.com");
        using var outsider = await app.SignIn("outsider", "outsider@gmail.com");
        var home = await CreateHousehold(owner, "Home");
        await CreateHousehold(owner, "Cabin");
        var invitation = await Invite(owner, home, " Member@GMAIL.COM ");
        Assert.Equal("member@gmail.com", invitation.GetProperty("email").GetString());
        Assert.Equal(app.Clock.GetUtcNow().AddDays(7), invitation.GetProperty("expiresAt").GetDateTimeOffset());
        var token = invitation.GetProperty("token").GetString();

        using var noToken = await invitee.PostAsJsonAsync(RedeemPath, new { token = (string?)null });
        using var truncatedToken = await invitee.PostAsJsonAsync(RedeemPath, new { token = token![..^1] });
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, truncatedToken.StatusCode);

        using var forwarded = await outsider.PostAsJsonAsync(RedeemPath, new { token });
        Assert.Equal(HttpStatusCode.NotFound, forwarded.StatusCode);
        Assert.Empty((await outsider.GetFromJsonAsync<JsonElement>("/api/households")).EnumerateArray());

        using var accepted = await invitee.PostAsJsonAsync(RedeemPath, new { token });
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal("no-store", accepted.Headers.CacheControl?.ToString());
        var result = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(home, result.GetProperty("id").GetGuid());
        Assert.Equal("Home", result.GetProperty("name").GetString());
        Assert.Equal("member", result.GetProperty("role").GetString());
        var membership = Assert.Single((await invitee.GetFromJsonAsync<JsonElement>("/api/households")).EnumerateArray());
        Assert.Equal(home, membership.GetProperty("id").GetGuid());

        using var reused = await invitee.PostAsJsonAsync(RedeemPath, new { token });
        Assert.Equal(HttpStatusCode.NotFound, reused.StatusCode);
    }

    [Fact]
    public async Task OnlyTheCurrentOwnerCanManagePendingInvitations()
    {
        await using var app = await TestApp.Start();
        using var anonymous = app.Browser();
        using var owner = await app.SignIn("owner", "owner@gmail.com");
        using var member = await app.SignIn("member", "member@gmail.com");
        using var outsider = await app.SignIn("outsider", "outsider@gmail.com");
        var home = await CreateHousehold(owner, "Home");
        var cottage = await CreateHousehold(owner, "Cabin");
        var memberInvitation = await Invite(owner, home, "member@gmail.com");
        var token = memberInvitation.GetProperty("token").GetString();
        Assert.Equal(HttpStatusCode.Created, (await member.PostAsJsonAsync(RedeemPath, new { token })).StatusCode);
        var invite = await Invite(owner, home, "guest@gmail.com");
        var path = Invitations(home);
        var invitePath = path + "/" + invite.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(path, new { email = "guest@gmail.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync(invitePath)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(RedeemPath, new { token })).StatusCode);
        foreach (var client in new[] { member, outsider })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path, new { email = "guest@gmail.com" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(invitePath)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(Invitations(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync(Invitations(cottage) + "/" + invite.GetProperty("id").GetGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync(path + "/" + memberInvitation.GetProperty("id").GetGuid())).StatusCode);
        var pending = Assert.Single((await owner.GetFromJsonAsync<JsonElement>(path)).EnumerateArray());
        Assert.Equal(invite.GetProperty("id").GetGuid(), pending.GetProperty("id").GetGuid());
        Assert.Equal("guest@gmail.com", pending.GetProperty("email").GetString());
        Assert.Equal(invite.GetProperty("expiresAt").GetDateTimeOffset(), pending.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.False(pending.TryGetProperty("token", out _));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(invitePath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync(invitePath)).StatusCode);
        using var guest = await app.SignIn("guest", "guest@gmail.com");
        Assert.Equal(HttpStatusCode.NotFound,
            (await guest.PostAsJsonAsync(RedeemPath, new { token = invite.GetProperty("token").GetString() })).StatusCode);
        Assert.Empty((await guest.GetFromJsonAsync<JsonElement>("/api/households")).EnumerateArray());
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>(path)).EnumerateArray());
    }

    [Fact]
    public async Task InvitationExpiresAtExactlySevenDays()
    {
        await using var app = await TestApp.Start();
        using var owner = await app.SignIn("owner", "owner@gmail.com");
        var home = await CreateHousehold(owner, "Home");
        var invitation = await Invite(owner, home, "late@gmail.com");
        var second = await Invite(owner, home, "early@gmail.com");
        app.Clock.Now = app.Clock.Now.AddDays(7).AddSeconds(-1);
        using var early = await app.SignIn("early", "early@gmail.com");
        Assert.Equal(HttpStatusCode.Created,
            (await early.PostAsJsonAsync(RedeemPath, new { token = second.GetProperty("token").GetString() })).StatusCode);

        app.Clock.Now = app.Clock.Now.AddSeconds(1);
        using var late = await app.SignIn("late", "late@gmail.com");
        Assert.Equal(HttpStatusCode.NotFound,
            (await late.PostAsJsonAsync(RedeemPath, new { token = invitation.GetProperty("token").GetString() })).StatusCode);
        Assert.Empty((await late.GetFromJsonAsync<JsonElement>("/api/households")).EnumerateArray());
        using var signedInOwner = await app.SignIn("owner", "owner@gmail.com");
        Assert.Empty((await signedInOwner.GetFromJsonAsync<JsonElement>(Invitations(home))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound,
            (await signedInOwner.DeleteAsync(Invitations(home) + "/" + invitation.GetProperty("id").GetGuid())).StatusCode);
    }

    [Fact]
    public async Task ConcurrentRedemptionSucceedsOnceAndASecondInviteDoesNotOverwriteMembership()
    {
        await using var app = await TestApp.Start();
        using var owner = await app.SignIn("owner", "owner@gmail.com");
        using var left = await app.SignIn("same", "same@managed.example", domain: "managed.example");
        using var right = await app.SignIn("same", "same@managed.example", domain: "managed.example");
        var home = await CreateHousehold(owner, "Home");
        var invitation = await Invite(owner, home, "same@managed.example");
        var token = invitation.GetProperty("token").GetString();

        var attempts = await Task.WhenAll(left.PostAsJsonAsync(RedeemPath, new { token }), right.PostAsJsonAsync(RedeemPath, new { token }));
        using var first = attempts[0];
        using var second = attempts[1];
        Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.NotFound);
        Assert.Single((await left.GetFromJsonAsync<JsonElement>("/api/households")).EnumerateArray());

        var duplicate = await Invite(owner, home, "same@managed.example");
        Assert.Equal(HttpStatusCode.NotFound,
            (await right.PostAsJsonAsync(RedeemPath, new { token = duplicate.GetProperty("token").GetString() })).StatusCode);
        var pending = Assert.Single((await owner.GetFromJsonAsync<JsonElement>(Invitations(home))).EnumerateArray());
        Assert.Equal(duplicate.GetProperty("id").GetGuid(), pending.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task InvalidInputsAndUnverifiedOrStaleEmailCannotGrantMembership()
    {
        await using var app = await TestApp.Start();
        using var owner = await app.SignIn("owner", "owner@gmail.com");
        var home = await CreateHousehold(owner, "Home");
        foreach (var email in new[] { null, "invalid", "Name <guest@gmail.com>", "nul\0@gmail.com", new string('x', 255) + "@gmail.com" })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(Invitations(home), new { email })).StatusCode);

        using var anonymous = app.Browser();
        using var unverified = await app.GoogleSignIn(anonymous, "guest", "guest@gmail.com", verified: false);
        Assert.Equal(HttpStatusCode.Unauthorized, unverified.StatusCode);
        using var external = await app.SignIn("external", "guest@personal.example");
        var unauthoritative = await Invite(owner, home, "guest@personal.example");
        Assert.Equal(HttpStatusCode.NotFound,
            (await external.PostAsJsonAsync(RedeemPath, new { token = unauthoritative.GetProperty("token").GetString() })).StatusCode);
        Assert.Empty((await external.GetFromJsonAsync<JsonElement>("/api/households")).EnumerateArray());

        using var original = await app.SignIn("changed", "old@gmail.com");
        var invitation = await Invite(owner, home, "old@gmail.com");
        using var changed = await app.SignIn("changed", "new@gmail.com");
        Assert.Equal(HttpStatusCode.NotFound,
            (await original.PostAsJsonAsync(RedeemPath, new { token = invitation.GetProperty("token").GetString() })).StatusCode);
        Assert.Empty((await original.GetFromJsonAsync<JsonElement>("/api/households")).EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await changed.PostAsJsonAsync(RedeemPath, new { token = "invalid-length" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await changed.PostAsJsonAsync(RedeemPath, new { token = new string('x', 43) })).StatusCode);
    }

    [Fact]
    public async Task RevocationAndRedemptionCannotBothSucceed()
    {
        await using var app = await TestApp.Start();
        using var owner = await app.SignIn("owner", "owner@gmail.com");
        using var guest = await app.SignIn("guest", "guest@gmail.com");
        var home = await CreateHousehold(owner, "Home");
        var invite = await Invite(owner, home, "guest@gmail.com");
        var token = invite.GetProperty("token").GetString();
        var invitePath = Invitations(home) + "/" + invite.GetProperty("id").GetGuid();

        var revoke = owner.DeleteAsync(invitePath);
        var redeem = guest.PostAsJsonAsync(RedeemPath, new { token });
        await Task.WhenAll(revoke, redeem);
        using var revoked = await revoke;
        using var redeemed = await redeem;
        if (revoked.StatusCode == HttpStatusCode.NoContent)
        {
            Assert.Equal(HttpStatusCode.NotFound, redeemed.StatusCode);
            Assert.Empty((await guest.GetFromJsonAsync<JsonElement>("/api/households")).EnumerateArray());
        }
        else
        {
            Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
            Assert.Equal(HttpStatusCode.Created, redeemed.StatusCode);
            var household = Assert.Single((await guest.GetFromJsonAsync<JsonElement>("/api/households")).EnumerateArray());
            Assert.Equal(home, household.GetProperty("id").GetGuid());
        }
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>(Invitations(home))).EnumerateArray());
    }

    private static string Invitations(Guid householdId) => $"/api/households/{householdId}/invitations";

    private static async Task<Guid> CreateHousehold(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/households", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> Invite(HttpClient client, Guid householdId, string email)
    {
        using var response = await client.PostAsJsonAsync(Invitations(householdId), new { email });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2030, 1, 1, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TestApp(PostgresTestDatabase database, WebApplicationFactory<Program> phaseOne,
        GoogleTestIdentityProvider google) : IAsyncDisposable
    {
        public TestClock Clock { get; } = new();
        private WebApplicationFactory<Program> Factory { get; set; } = null!;

        public static async Task<TestApp> Start()
        {
            var db = new PostgresTestDatabase();
            await db.Initialize();
            var phaseOne = db.CreateFactory();
            var google = new GoogleTestIdentityProvider();
            var app = new TestApp(db, phaseOne, google);
            app.Factory = google.Configure(phaseOne, app.Clock);
            return app;
        }

        public HttpClient Browser() =>
            Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });

        public async Task<HttpClient> SignIn(string subject, string email, string? domain = null)
        {
            var client = Browser();
            using var response = await GoogleSignIn(client, subject, email, domain: domain);
            response.EnsureSuccessStatusCode();
            return client;
        }

        public Task<HttpResponseMessage> GoogleSignIn(HttpClient client, string subject, string email,
            bool verified = true, string? domain = null) =>
            client.PostAsJsonAsync("/api/auth/google", new { credential = google.Credential(subject, email, verified, domain: domain) });

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            google.Dispose();
            await phaseOne.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
