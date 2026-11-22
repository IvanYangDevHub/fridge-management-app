using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FreshDays.Tests;

public class InventoryApiTests
{
    [Fact]
    public async Task CreatingAnItemReturnsItsVersionAndTheHouseholdCanReadIt()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var phaseOne = database.CreateFactory();
        using var google = new GoogleTestIdentityProvider();
        await using var factory = google.Configure(phaseOne);
        using var alice = Browser(factory);
        await SignIn(alice, google.Credential("alice", "alice@gmail.com"));
        var path = await CreateHousehold(alice, "Home");

        using var created = await alice.PostAsJsonAsync(path, new { name = "鮮奶", expiresAt = "2027-01-01", quantity = 2 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("鮮奶", item.GetProperty("name").GetString());
        Assert.Equal(1, item.GetProperty("version").GetInt64());
        Assert.Equal(2, item.GetProperty("quantity").GetInt32());

        using var response = await alice.GetAsync(path);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var read = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(item.GetProperty("id").GetString(), read.GetProperty("id").GetString());
        Assert.Equal(1, read.GetProperty("version").GetInt64());
        Assert.Equal("2027-01-01", read.GetProperty("expiresAt").GetString());
    }


    [Fact]
    public async Task TwoMembersCanChangeDistinctItemsAndReloadAfterAVersionConflict()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var phaseOne = database.CreateFactory();
        using var google = new GoogleTestIdentityProvider();
        await using var factory = google.Configure(phaseOne);
        using var alice = Browser(factory);
        using var bob = Browser(factory);
        using var outsider = Browser(factory);
        await SignIn(alice, google.Credential("alice", "alice@gmail.com"));
        await SignIn(bob, google.Credential("bob", "bob@gmail.com"));
        await SignIn(outsider, google.Credential("outsider", "outsider@gmail.com"));
        var household = await CreateHousehold(alice, "Home");
        var isolated = await CreateHousehold(outsider, "Office");
        var householdId = Guid.Parse(household.Split('/')[3]);
        var bobId = (await bob.GetFromJsonAsync<JsonElement>("/api/auth/session")).GetProperty("userId").GetGuid();
        // Establish shared membership until the invitation endpoints are integrated.
        await using var source = Npgsql.NpgsqlDataSource.Create(database.IsolatedConnectionString);
        await using (var membership = source.CreateCommand(
            "INSERT INTO household_members (household_id, user_id, role) VALUES (@household, @user, 'member')"))
        {
            membership.Parameters.AddWithValue("household", householdId);
            membership.Parameters.AddWithValue("user", bobId);
            await membership.ExecuteNonQueryAsync();
        }

        var milk = ItemPath(household, await CreateItem(alice, household, "Milk"));
        var eggs = ItemPath(household, await CreateItem(bob, household, "Eggs"));
        var aliceUpdate = alice.PutAsJsonAsync(milk, new { version = 1, name = "Soy milk", quantity = 3 });
        var bobUpdate = bob.PutAsJsonAsync(eggs, new { version = 1, name = "Free range eggs", quantity = 6 });
        foreach (var response in await Task.WhenAll(aliceUpdate, bobUpdate))
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64());
            }
        }

        using var first = await alice.GetAsync(household);
        var shared = (await first.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToArray();
        Assert.Equal(new[] { "Free range eggs", "Soy milk" }, shared.Select(item => item.GetProperty("name").GetString()).Order().ToArray());
        var competingWrites = await Task.WhenAll(
            alice.PutAsJsonAsync(milk, new { version = 2, name = "Oat milk" }),
            bob.PutAsJsonAsync(milk, new { version = 2, name = "Rice milk" }));
        using var winner = Assert.Single(competingWrites, response => response.StatusCode == HttpStatusCode.OK);
        using var loser = Assert.Single(competingWrites, response => response.StatusCode == HttpStatusCode.Conflict);
        var winningItem = await winner.Content.ReadFromJsonAsync<JsonElement>();

        var reloaded = await bob.GetFromJsonAsync<JsonElement>(household);
        var current = Assert.Single(reloaded.EnumerateArray(), item => item.GetProperty("id").GetString() == winningItem.GetProperty("id").GetString());
        Assert.Equal(winningItem.GetProperty("name").GetString(), current.GetProperty("name").GetString());
        Assert.Equal(3, current.GetProperty("quantity").GetInt32());
        Assert.Equal(3, current.GetProperty("version").GetInt64());

        using var outsideWrite = await outsider.PutAsJsonAsync(milk, new { version = 3, name = "Stolen" });
        Assert.Equal(HttpStatusCode.Forbidden, outsideWrite.StatusCode);
        using var outsideRead = await outsider.GetAsync(household);
        Assert.Equal(HttpStatusCode.Forbidden, outsideRead.StatusCode);
        Assert.Empty((await outsider.GetFromJsonAsync<JsonElement>(isolated)).EnumerateArray());
    }

    [Fact]
    public async Task AClaimedLegacyItemCanBeEditedAndDeletedWithoutChangingItsId()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var phaseOne = database.CreateFactory();
        using var paired = phaseOne.CreateClient();
        paired.DefaultRequestHeaders.Add("x-household-code", "test-code");
        const string legacyId = "pantry/oil ?%#";
        using var seeded = await paired.PutAsJsonAsync("/api/inventory", new[] { new { id = legacyId, name = "Oil", expiryNote = "Original label" } });
        Assert.Equal(HttpStatusCode.NoContent, seeded.StatusCode);
        var legacy = Assert.Single((await paired.GetFromJsonAsync<JsonElement>("/api/inventory")).EnumerateArray());
        Assert.False(legacy.TryGetProperty("version", out _));

        using var google = new GoogleTestIdentityProvider();
        await using var factory = google.Configure(phaseOne);
        using var client = Browser(factory);
        await SignIn(client, google.Credential("owner"));
        using var claimed = await client.PostAsync("/api/households/legacy/claim", null);
        Assert.Equal(HttpStatusCode.Created, claimed.StatusCode);
        var household = Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/households")).EnumerateArray());
        var path = $"/api/households/{household.GetProperty("id").GetGuid()}/inventory";
        var itemPath = ItemPath(path, legacyId);
        using var replaced = await client.PutAsJsonAsync(path, Array.Empty<object>());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, replaced.StatusCode);
        using var updated = await client.PutAsJsonAsync(itemPath, new { version = 1, name = "Olive oil" });
        var current = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(legacyId, current.GetProperty("id").GetString());
        Assert.Equal("Original label", current.GetProperty("expiryNote").GetString());
        using var stale = await client.DeleteAsync(itemPath + "&version=1");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var deleted = await client.DeleteAsync(itemPath + "&version=2");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var alreadyDeleted = await client.DeleteAsync(itemPath + "&version=2");
        Assert.Equal(HttpStatusCode.Conflict, alreadyDeleted.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>(path)).EnumerateArray());
    }


    [Fact]
    public async Task InvalidWritesCannotChangeVersionedInventory()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var phaseOne = database.CreateFactory();
        using var google = new GoogleTestIdentityProvider();
        await using var factory = google.Configure(phaseOne);
        using var client = Browser(factory);
        using var anonymous = Browser(factory);
        await SignIn(client, google.Credential("owner"));
        var path = await CreateHousehold(client, "Home");
        var item = ItemPath(path, await CreateItem(client, path, "Milk"));

        using var unauthenticated = await anonymous.PostAsJsonAsync(path, new { name = "Rice" });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        var invalidBodies = new[]
        {
            "[",
            "[]",
            "{}",
            """{"name":12}""",
            """{"name":" "}""",
            JsonSerializer.Serialize(new { name = new string('n', 101) }),
            """{"name":"Rice","expiresAt":12}""",
            """{"name":"Rice","expiresAt":"2027-02-30"}""",
            """{"name":"Rice","extra":"\u0000"}""",
            """{"name":"\uD800"}""",
        };
        foreach (var invalid in invalidBodies)
        {
            using var body = new StringContent(invalid);
            using var response = await client.PostAsync(path, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var tooLarge = new StringContent(new string(' ', 1_048_577));
        using var rejected = await client.PostAsync(path, tooLarge);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);

        foreach (var invalid in new[] { "{}", """{"name":"Rice"}""", """{"name":"Rice","version":"1"}""",
                     """{"name":"Rice","version":1.5}""", """{"name":"Rice","version":0}""" })
        {
            using var body = new StringContent(invalid);
            using var response = await client.PutAsync(item, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var missingId = await client.PutAsJsonAsync(path + "/item?id=%20", new { version = 1, name = "Rice" });
        Assert.Equal(HttpStatusCode.BadRequest, missingId.StatusCode);
        using var nulId = await client.DeleteAsync(path + "/item?id=%00&version=1");
        Assert.Equal(HttpStatusCode.BadRequest, nulId.StatusCode);
        using var invalidVersion = await client.DeleteAsync(item + "&version=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalidVersion.StatusCode);

        var remaining = Assert.Single((await client.GetFromJsonAsync<JsonElement>(path)).EnumerateArray());
        Assert.Equal("Milk", remaining.GetProperty("name").GetString());
        Assert.Equal(1, remaining.GetProperty("version").GetInt64());

        using var cleared = await client.PutAsJsonAsync(item, new { name = "Rice", version = 1, expiresAt = (string?)null });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("expiresAt").ValueKind);
    }


    private static async Task<string> CreateItem(HttpClient client, string path, string name)
    {
        using var response = await client.PostAsJsonAsync(path, new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static string ItemPath(string path, string id) => $"{path}/item?id={Uri.EscapeDataString(id)}";

    private static HttpClient Browser(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });

    private static async Task SignIn(HttpClient client, string credential)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/google", new { credential });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> CreateHousehold(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/households", new { name });
        response.EnsureSuccessStatusCode();
        var household = await response.Content.ReadFromJsonAsync<JsonElement>();
        return $"/api/households/{household.GetProperty("id").GetGuid()}/inventory";
    }
}
