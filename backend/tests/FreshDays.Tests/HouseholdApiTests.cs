using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FreshDays.Tests;

public class HouseholdApiTests
{
    [Fact]
    public async Task SignedInUsersCanCreateAndListOnlyTheirOwnHouseholds()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var phaseOne = database.CreateFactory();
        using var google = new GoogleTestIdentityProvider();
        await using var factory = google.Configure(phaseOne);
        using var alice = Browser(factory);
        using var bob = Browser(factory);

        Assert.Equal(HttpStatusCode.Unauthorized, (await alice.GetAsync("/api/households")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await alice.PostAsJsonAsync("/api/households", new { name = "Home" })).StatusCode);
        await SignIn(alice, google.Credential("alice", "alice@gmail.com"));
        await SignIn(bob, google.Credential("bob", "bob@gmail.com"));

        Assert.Equal(0, (await alice.GetFromJsonAsync<JsonElement>("/api/households")).GetArrayLength());
        var home = await Create(alice, "  Home  ");
        Assert.Equal("Home", home.GetProperty("name").GetString());
        Assert.Equal("owner", home.GetProperty("role").GetString());
        var office = await Create(bob, "Office");

        var aliceList = await alice.GetAsync("/api/households");
        Assert.Equal("no-store", aliceList.Headers.CacheControl?.ToString());
        var aliceHouseholds = await aliceList.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(home.GetProperty("id").GetGuid(), Assert.Single(aliceHouseholds.EnumerateArray()).GetProperty("id").GetGuid());
        var bobHouseholds = await bob.GetFromJsonAsync<JsonElement>("/api/households");
        Assert.Equal(office.GetProperty("id").GetGuid(), Assert.Single(bobHouseholds.EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("inventory", "expiresAt", "2027-01-01")]
    [InlineData("shopping", "completed", true)]
    public async Task HouseholdListsFollowTheExplicitScopeAndRejectAnotherUser(string list, string field, object value)
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var phaseOne = database.CreateFactory();
        using var google = new GoogleTestIdentityProvider();
        await using var factory = google.Configure(phaseOne);
        using var alice = Browser(factory);
        using var bob = Browser(factory);
        using var anonymous = Browser(factory);

        await SignIn(alice, google.Credential("alice", "alice@gmail.com"));
        await SignIn(bob, google.Credential("bob", "bob@gmail.com"));
        var home = await Create(alice, "Home");
        var cottage = await Create(alice, "Cottage");
        var office = await Create(bob, "Office");
        var householdPaths = new[] { home, cottage, office }.Select(household =>
            $"/api/households/{household.GetProperty("id").GetGuid()}/{list}").ToArray();

        var homePath = householdPaths[0];
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(homePath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync(homePath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PutAsJsonAsync(homePath, Array.Empty<object>())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync(householdPaths[2])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync($"/api/households/{Guid.NewGuid()}/{list}")).StatusCode);

        var original = new Dictionary<string, object> { ["id"] = "same-id", ["name"] = "Home item", [field] = value };
        var other = new Dictionary<string, object> { ["id"] = "same-id", ["name"] = "Cottage item", [field] = value };
        Assert.Equal(HttpStatusCode.NoContent, (await alice.PutAsJsonAsync(homePath, new[] { original })).StatusCode);
        Assert.Equal(0, (await alice.GetFromJsonAsync<JsonElement>(householdPaths[1])).GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent, (await alice.PutAsJsonAsync(householdPaths[1], new[] { other })).StatusCode);
        Assert.Equal(0, (await bob.GetFromJsonAsync<JsonElement>(householdPaths[2])).GetArrayLength());

        using var response = await alice.GetAsync(homePath);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var homeItems = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Home item", Assert.Single(homeItems.EnumerateArray()).GetProperty("name").GetString());
        var cottageItems = await alice.GetFromJsonAsync<JsonElement>(householdPaths[1]);
        Assert.Equal("Cottage item", Assert.Single(cottageItems.EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task InvalidHouseholdNamesAreRejectedWithoutCreatingMemberships()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var phaseOne = database.CreateFactory();
        using var google = new GoogleTestIdentityProvider();
        await using var factory = google.Configure(phaseOne);
        using var client = Browser(factory);
        await SignIn(client, google.Credential("alice", "alice@gmail.com"));

        foreach (var name in new[] { null, "", "  ", new string('長', 101), "Invalid\0name" })
        {
            using var response = await client.PostAsJsonAsync("/api/households", new { name });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/households")).EnumerateArray());
    }

    [Fact]
    public async Task InvalidScopedListWritesPreserveExistingInventory()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var phaseOne = database.CreateFactory();
        using var google = new GoogleTestIdentityProvider();
        await using var factory = google.Configure(phaseOne);
        using var client = Browser(factory);
        await SignIn(client, google.Credential("alice", "alice@gmail.com"));
        var home = await Create(client, "Home");
        var path = $"/api/households/{home.GetProperty("id").GetGuid()}/inventory";
        var original = new[] { new { id = "original", name = "牛奶", expiresAt = "2027-01-01" } };
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(path, original)).StatusCode);

        var invalidBodies = new[]
        {
            "[",
            "{}",
            "[" + string.Join(',', Enumerable.Repeat("{}", 501)) + "]",
            """[{"id":"date","name":"鹽","expiresAt":12}]""",
            """[{"id":"date","name":"鹽","expiresAt":"2027-02-30"}]""",
        };
        foreach (var invalid in invalidBodies)
        {
            using var body = new StringContent(invalid);
            using var response = await client.PutAsync(path, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var tooLarge = new StringContent(new string(' ', 1_048_577));
        using var rejected = await client.PutAsync(path, tooLarge);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
        var remaining = await client.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal("original", Assert.Single(remaining.EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task ClaimingTheLegacyHouseholdExposesMigratedListsOnlyToItsOwner()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var phaseOne = database.CreateFactory();
        using var paired = phaseOne.CreateClient();
        paired.DefaultRequestHeaders.Add("x-household-code", "test-code");
        Assert.Equal(HttpStatusCode.NotFound, (await paired.GetAsync("/api/households")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await paired.PutAsJsonAsync("/api/inventory", new[]
        {
            new { id = "legacy-milk", name = "鮮奶" }
        })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await paired.PutAsJsonAsync("/api/shopping", new[]
        {
            new { id = "legacy-rice", name = "米", completed = false }
        })).StatusCode);

        using var google = new GoogleTestIdentityProvider();
        await using var factory = google.Configure(phaseOne);
        using var claimant = Browser(factory);
        using var outsider = Browser(factory);
        await SignIn(claimant, google.Credential("owner"));
        await SignIn(outsider, google.Credential("other", "other@gmail.com"));

        using var claimed = await claimant.PostAsync("/api/households/legacy/claim", null);
        Assert.Equal(HttpStatusCode.Created, claimed.StatusCode);
        var membership = Assert.Single((await claimant.GetFromJsonAsync<JsonElement>("/api/households")).EnumerateArray());
        Assert.Equal("owner", membership.GetProperty("role").GetString());
        var root = $"/api/households/{membership.GetProperty("id").GetGuid()}";
        var inventory = Assert.Single((await claimant.GetFromJsonAsync<JsonElement>(root + "/inventory")).EnumerateArray());
        var shopping = Assert.Single((await claimant.GetFromJsonAsync<JsonElement>(root + "/shopping")).EnumerateArray());
        Assert.Equal("legacy-milk", inventory.GetProperty("id").GetString());
        Assert.Equal("legacy-rice", shopping.GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(root + "/inventory")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(root + "/shopping")).StatusCode);
    }

    private static HttpClient Browser(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });

    private static async Task SignIn(HttpClient client, string credential)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/google", new { credential });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> Create(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/households", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
