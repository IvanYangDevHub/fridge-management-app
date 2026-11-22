using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FreshDays.Tests;

public class AuthApiTests
{
    [Fact]
    public async Task FirstCutoverRetainsThePairingCodeWithoutGoogle()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var factory = database.CreateFactory();
        using var client = factory.CreateClient();

        var mode = await client.GetFromJsonAsync<JsonElement>("/api/auth/config");
        Assert.Equal("phase-one", mode.GetProperty("mode").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/auth/session")).StatusCode);
        client.DefaultRequestHeaders.Add("x-household-code", "test-code");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/inventory")).StatusCode);
    }

    [Fact]
    public async Task SecondCutoverRejectsPairingCodeAndAnnouncesGoogle()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var phaseOne = database.CreateFactory();
        await using var factory = PhaseTwo(phaseOne);
        using var client = factory.CreateClient();

        var mode = await client.GetFromJsonAsync<JsonElement>("/api/auth/config");
        Assert.Equal("phase-two", mode.GetProperty("mode").GetString());
        Assert.Equal("freshdays-test-client", mode.GetProperty("googleClientId").GetString());
        client.DefaultRequestHeaders.Add("x-household-code", "test-code");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/inventory")).StatusCode);
    }


    [Fact]
    public async Task SignedGoogleAccountReceivesARevocableSessionAndKeepsItsIdentityWhenEmailChanges()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var phaseOne = database.CreateFactory();
        using var google = new GoogleTestIdentityProvider();
        await using var factory = google.Configure(phaseOne);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/session")).StatusCode);
        var firstLogin = await client.PostAsJsonAsync("/api/auth/google", new { credential = google.Credential("subject") });
        Assert.Equal(HttpStatusCode.OK, firstLogin.StatusCode);
        var cookie = Assert.Single(firstLogin.Headers.GetValues("Set-Cookie"));
        Assert.Contains("; secure", cookie);
        Assert.Contains("; httponly", cookie);
        Assert.Contains("; samesite=lax", cookie);
        var current = await client.GetFromJsonAsync<JsonElement>("/api/auth/session");
        var firstId = current.GetProperty("userId").GetGuid();
        Assert.Equal("owner@gmail.com", current.GetProperty("email").GetString());

        var changedEmail = await client.PostAsJsonAsync("/api/auth/google",
            new { credential = google.Credential("subject", "new@gmail.com") });
        var updated = await changedEmail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(firstId, updated.GetProperty("userId").GetGuid());
        Assert.Equal("new@gmail.com", updated.GetProperty("email").GetString());

        var revokedCookie = Assert.Single(changedEmail.Headers.GetValues("Set-Cookie")).Split(';')[0];
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/session")).StatusCode);
        using var replay = new HttpRequestMessage(HttpMethod.Get, "/api/auth/session");
        replay.Headers.Add("Cookie", revokedCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(replay)).StatusCode);
    }

    [Fact]
    public async Task OnlyTheConfiguredGoogleEmailCanClaimAndOneRequestWinsTheRace()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var phaseOne = database.CreateFactory();
        using var google = new GoogleTestIdentityProvider();
        await using var factory = google.Configure(phaseOne);
        using var claimant = SignedInBrowser(factory);
        using var other = SignedInBrowser(factory);
        using var differentSubject = SignedInBrowser(factory);

        const string claimPath = "/api/households/legacy/claim";
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.PostAsync(claimPath, null)).StatusCode);
        var otherSession = await SignIn(other, google.Credential("other", "other@gmail.com"));
        Assert.False(otherSession.GetProperty("canClaimLegacy").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync(claimPath, null)).StatusCode);

        var eligible = await SignIn(claimant, google.Credential("intended-owner", "OWNER@gmail.com"));
        Assert.True(eligible.GetProperty("canClaimLegacy").GetBoolean());
        var secondEligible = await SignIn(differentSubject, google.Credential("different-subject"));
        Assert.NotEqual(eligible.GetProperty("userId").GetGuid(), secondEligible.GetProperty("userId").GetGuid());

        var results = await Task.WhenAll(claimant.PostAsync(claimPath, null), differentSubject.PostAsync(claimPath, null));
        var winner = Assert.Single(results, result => result.StatusCode == HttpStatusCode.Created);
        var household = await winner.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("00000000-0000-0000-0000-000000000001", household.GetProperty("householdId").GetString());
        Assert.Single(results, result => result.StatusCode == HttpStatusCode.Conflict);

        var afterClaim = await claimant.GetFromJsonAsync<JsonElement>("/api/auth/session");
        Assert.False(afterClaim.GetProperty("canClaimLegacy").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await claimant.PostAsync(claimPath, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync(claimPath, null)).StatusCode);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("company.example", true)]
    public async Task AThirdPartyAddressCanClaimOnlyWhenGoogleHostsIt(string? hostedDomain, bool canClaim)
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var phaseOne = database.CreateFactory();
        using var google = new GoogleTestIdentityProvider();
        await using var factory = google.Configure(phaseOne, ownerEmail: "owner@company.example");
        using var client = SignedInBrowser(factory);

        var session = await SignIn(client, google.Credential("subject", "owner@company.example", domain: hostedDomain));
        Assert.Equal(canClaim, session.GetProperty("canClaimLegacy").GetBoolean());
        var response = await client.PostAsync("/api/households/legacy/claim", null);
        Assert.Equal(canClaim ? HttpStatusCode.Created : HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("bad-signature")]
    [InlineData("bad-audience")]
    [InlineData("wrong-issuer")]
    [InlineData("expired-token")]
    [InlineData("email-unverified")]
    [InlineData("email-verification-missing")]
    [InlineData("subject-missing")]
    [InlineData("email-missing")]
    [InlineData("email-empty")]
    [InlineData("empty")]
    public async Task InvalidGoogleCredentialsNeverStartAnApplicationSession(string invalidity)
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var phaseOne = database.CreateFactory();
        using var google = new GoogleTestIdentityProvider();
        await using var factory = google.Configure(phaseOne);
        using var client = SignedInBrowser(factory);

        var token = invalidity switch
        {
            "bad-signature" => google.Credential("subject", forged: true),
            "bad-audience" => google.Credential("subject", audience: GoogleTestIdentityProvider.Audience + "/"),
            "wrong-issuer" => google.Credential("subject", issuer: "https://another.example"),
            "expired-token" => google.Credential("subject", expires: DateTimeOffset.UtcNow.AddMinutes(-1)),
            "email-unverified" => google.Credential("subject", verified: false),
            "email-verification-missing" => google.Credential("subject", verified: null),
            "subject-missing" => google.Credential(null),
            "email-missing" => google.Credential("subject", null),
            "email-empty" => google.Credential("subject", " "),
            _ => "",
        };

        using var result = await client.PostAsJsonAsync("/api/auth/google", new { credential = token });
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.False(result.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/session")).StatusCode);
    }

    [Fact]
    public async Task ForgedAndExpiredSessionCookiesCannotAuthorizeAClaim()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var phaseOne = database.CreateFactory();
        using var google = new GoogleTestIdentityProvider();
        var time = new AdjustableTime(DateTimeOffset.UtcNow);
        await using var factory = google.Configure(phaseOne, time);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new("https://localhost"), HandleCookies = false,
        });

        var login = await client.PostAsJsonAsync("/api/auth/google", new { credential = google.Credential("subject") });
        login.EnsureSuccessStatusCode();
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];
        var forged = cookie[..^1] + (cookie[^1] == 'a' ? 'b' : 'a');

        async Task<HttpStatusCode> Claim(string session)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/households/legacy/claim");
            request.Headers.Add("Cookie", session);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await Claim(forged));
        time.Now = time.Now.AddDays(7);
        Assert.Equal(HttpStatusCode.Unauthorized, await Claim(cookie));
        Assert.True((await SignIn(client, google.Credential("subject"))).GetProperty("canClaimLegacy").GetBoolean());
    }

    [Fact]
    public async Task BlocksLegacyClaimFromAnotherOriginOnTheSameSite()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var phaseOne = database.CreateFactory();
        using var google = new GoogleTestIdentityProvider();
        await using var factory = google.Configure(phaseOne);
        using var browser = SignedInBrowser(factory);
        await SignIn(browser, google.Credential("subject"));

        browser.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-site");
        Assert.True((await browser.GetFromJsonAsync<JsonElement>("/api/auth/session")).GetProperty("canClaimLegacy").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/api/households/legacy/claim", null)).StatusCode);

        browser.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        browser.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        Assert.Equal(HttpStatusCode.Created, (await browser.PostAsync("/api/households/legacy/claim", null)).StatusCode);
    }
    [Theory]
    [InlineData(false, "HouseholdCode", "HOUSEHOLD_CODE")]
    [InlineData(false, "Line:ChannelSecret", "LINE_CHANNEL_SECRET")]
    [InlineData(false, "Line:AccessToken", "LINE_CHANNEL_ACCESS_TOKEN")]
    [InlineData(true, "Auth:GoogleClientId", "Auth__GoogleClientId")]
    public async Task IncompleteCutoverConfigurationFailsStartup(bool phaseTwo, string key, string requiredSetting)
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var baseFactory = database.CreateFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, settings) =>
            settings.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Product:PhaseTwo"] = phaseTwo.ToString(),
                [key] = "",
            })));
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains(requiredSetting, error.Message);
    }

    [Fact]
    public async Task FirstCutoverCanStartWithTheReminderWorkerEnabled()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var baseFactory = database.CreateFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, settings) =>
            settings.AddInMemoryCollection(new Dictionary<string, string?> { ["DisableWorker"] = "false" })));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    private sealed class AdjustableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static HttpClient SignedInBrowser(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });

    private static async Task<JsonElement> SignIn(HttpClient client, string credential)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/google", new { credential });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static WebApplicationFactory<Program> PhaseTwo(WebApplicationFactory<Program> phaseOne)
    {
        return phaseOne.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, settings) =>
            settings.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Product:PhaseTwo"] = "true",
                ["HouseholdCode"] = "",
                ["Auth:GoogleClientId"] = "freshdays-test-client",
                ["Auth:LegacyOwnerEmail"] = "owner@gmail.com",
            })));
    }
}
