using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FreshDays.Tests;

public sealed partial class MembershipApiTests
{
    [Fact]
    public async Task LeavingRevokesHouseholdAccessInEveryActiveSessionAndPreservesLists()
    {
        await using var app = await TestApp.Start();
        using var owner = await app.SignIn("owner");
        using var member = await app.SignIn("member");
        using var secondSession = await app.SignIn("member");
        var household = await CreateHousehold(owner, "Home");
        var other = await CreateHousehold(member, "Other home");
        await Admit(owner, member, household, "member");
        var entry = new { name = "Apples" };
        using var saved = await member.PostAsJsonAsync(household + "/inventory", entry);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);

        using var departed = await member.DeleteAsync(household + "/members/me");
        Assert.Equal(HttpStatusCode.NoContent, departed.StatusCode);
        Assert.Equal("no-store", departed.Headers.CacheControl?.ToString());
        foreach (var session in new[] { member, secondSession })
        {
            Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/auth/session")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await session.GetAsync(household + "/inventory")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await session.PostAsJsonAsync(household + "/shopping", new { name = "Rice", completed = false })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await session.GetAsync(other + "/inventory")).StatusCode);
            Assert.Single((await session.GetFromJsonAsync<JsonElement>("/api/households")).EnumerateArray());
        }
        var food = Assert.Single((await owner.GetFromJsonAsync<JsonElement>(household + "/inventory")).EnumerateArray());
        Assert.Equal("Apples", food.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync(household + "/members/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync(household + "/members/me")).StatusCode);
    }

    [Fact]
    public async Task OnlyTheOwnerCanRemoveAnotherMemberWithoutAffectingOtherHouseholds()
    {
        await using var app = await TestApp.Start();
        using var anonymous = app.Browser();
        using var owner = await app.SignIn("owner");
        using var member = await app.SignIn("member");
        using var outsider = await app.SignIn("outsider");
        var ownerId = await UserId(owner);
        var memberId = await UserId(member);
        var household = await CreateHousehold(owner, "Home");
        var other = await CreateHousehold(owner, "Other home");
        await Admit(owner, member, household, "member");
        await Admit(owner, member, other, "member");
        var target = household + "/members/" + memberId;

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync(target)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync(target)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.DeleteAsync(target)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await owner.DeleteAsync($"/api/households/{Guid.NewGuid()}/members/{memberId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync(household + "/members/" + ownerId)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(target)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync(target)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(household + "/shopping")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(other + "/shopping")).StatusCode);
    }

    [Fact]
    public async Task TransferringToAnExistingMemberImmediatelyChangesOwnerPermissions()
    {
        await using var app = await TestApp.Start();
        using var owner = await app.SignIn("owner");
        using var member = await app.SignIn("member");
        using var outsider = await app.SignIn("outsider");
        var ownerId = await UserId(owner);
        var memberId = await UserId(member);
        var outsiderId = await UserId(outsider);
        var household = await CreateHousehold(owner, "Home");
        var other = await CreateHousehold(owner, "Other home");
        await Admit(owner, member, household, "member");
        await Admit(owner, outsider, other, "outsider");
        var rosterPath = household + "/members";
        var transferPath = rosterPath + "/" + memberId + "/transfer";

        using var roster = await owner.GetAsync(rosterPath);
        Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
        Assert.Equal("no-store", roster.Headers.CacheControl?.ToString());
        var entries = (await roster.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        var ownerEntry = Assert.Single(entries, entry => entry.GetProperty("role").GetString() == "owner");
        Assert.Equal(ownerId, ownerEntry.GetProperty("userId").GetGuid());
        var memberEntry = Assert.Single(entries, entry => entry.GetProperty("role").GetString() == "member");
        Assert.Equal("member@gmail.com", memberEntry.GetProperty("email").GetString());
        Assert.Equal(memberId, memberEntry.GetProperty("userId").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(rosterPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync(transferPath, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync(rosterPath + "/" + outsiderId + "/transfer", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync(rosterPath + "/" + ownerId + "/transfer", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync(transferPath, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(rosterPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(household + "/invitations")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(household + "/invitations")).StatusCode);
        var currentOwner = await CurrentOwner(member, household);
        Assert.Equal(memberId, currentOwner);
        Assert.Equal(HttpStatusCode.Conflict, (await member.DeleteAsync(rosterPath + "/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(rosterPath + "/me")).StatusCode);
    }

    [Fact]
    public async Task ConcurrentRemovalAndTransferLeaveExactlyOneOwner()
    {
        await using var app = await TestApp.Start();
        using var owner = await app.SignIn("owner");
        using var secondOwnerSession = await app.SignIn("owner");
        using var target = await app.SignIn("target");
        var ownerId = await UserId(owner);
        var targetId = await UserId(target);
        var household = await CreateHousehold(owner, "Home");
        await Admit(owner, target, household, "target");
        var targetPath = household + "/members/" + targetId;

        var attempts = await Task.WhenAll(owner.DeleteAsync(targetPath), secondOwnerSession.PostAsync(targetPath + "/transfer", null));
        using var removal = attempts[0];
        using var transfer = attempts[1];
        if (removal.StatusCode == HttpStatusCode.NoContent)
        {
            Assert.Equal(HttpStatusCode.NotFound, transfer.StatusCode);
            Assert.Equal(ownerId, await CurrentOwner(owner, household));
            Assert.Equal(HttpStatusCode.Forbidden, (await target.GetAsync(household + "/shopping")).StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Forbidden, removal.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, transfer.StatusCode);
            Assert.Equal(targetId, await CurrentOwner(target, household));
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(household + "/shopping")).StatusCode);
        }
    }

    private static async Task<Guid> CurrentOwner(HttpClient client, string household)
    {
        var roster = (await client.GetFromJsonAsync<JsonElement>(household + "/members")).EnumerateArray();
        return Assert.Single(roster, entry => entry.GetProperty("role").GetString() == "owner")
            .GetProperty("userId").GetGuid();
    }

    private static async Task<Guid> UserId(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/auth/session")).GetProperty("userId").GetGuid();

    private static async Task<string> CreateHousehold(HttpClient owner, string name)
    {
        using var response = await owner.PostAsJsonAsync("/api/households", new { name });
        response.EnsureSuccessStatusCode();
        var household = await response.Content.ReadFromJsonAsync<JsonElement>();
        return $"/api/households/{household.GetProperty("id").GetGuid()}";
    }

    private static async Task Admit(HttpClient owner, HttpClient member, string household, string subject)
    {
        using var invitation = await owner.PostAsJsonAsync(household + "/invitations", new { email = subject + "@gmail.com" });
        invitation.EnsureSuccessStatusCode();
        var body = await invitation.Content.ReadFromJsonAsync<JsonElement>();
        using var admission = await member.PostAsJsonAsync("/api/households/invitations/redeem",
            new { token = body.GetProperty("token").GetString() });
        admission.EnsureSuccessStatusCode();
    }

    private sealed class TestApp(PostgresTestDatabase database, WebApplicationFactory<Program> phaseOne,
        WebApplicationFactory<Program> phaseTwo, GoogleTestIdentityProvider google) : IAsyncDisposable
    {
        public static async Task<TestApp> Start()
        {
            var database = new PostgresTestDatabase();
            await database.Initialize();
            var phaseOne = database.CreateFactory();
            var google = new GoogleTestIdentityProvider();
            return new(database, phaseOne, google.Configure(phaseOne), google);
        }

        public string ConnectionString => database.IsolatedConnectionString;

        public HttpClient LegacyBrowser()
        {
            var browser = phaseOne.CreateClient();
            browser.DefaultRequestHeaders.Add("x-household-code", "test-code");
            return browser;
        }

        public HttpClient Browser() =>
            phaseTwo.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });

        public async Task<HttpClient> SignIn(string subject)
        {
            var client = Browser();
            using var response = await client.PostAsJsonAsync("/api/auth/google",
                new { credential = google.Credential(subject, subject + "@gmail.com") });
            response.EnsureSuccessStatusCode();
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await phaseTwo.DisposeAsync();
            google.Dispose();
            await phaseOne.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
