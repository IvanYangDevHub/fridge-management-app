using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace FreshDays.Tests;

public class ShoppingApiTests
{
    [Fact]
    public async Task ShoppingItemsCanBeAddedAndReadWithTheirInitialVersions()
    {
        await using var household = await ShoppingHousehold.Create();
        using var created = await household.Owner.PostAsJsonAsync(household.Path, new
        {
            name = "米", completed = false,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(Guid.TryParse(item.GetProperty("id").GetString(), out _));
        Assert.Equal("米", item.GetProperty("name").GetString());
        Assert.False(item.GetProperty("completed").GetBoolean());
        Assert.True(item.GetProperty("version").GetInt64() > 0);

        using var response = await household.Owner.GetAsync(household.Path);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(item.GetRawText(), Assert.Single(
            (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()).GetRawText());
    }

    [Fact]
    public async Task MembersCanUpdateDifferentItemsAndSeeBothChangesOnTheirNextRead()
    {
        await using var household = await ShoppingHousehold.Create();
        using var member = await household.Join("member");
        var rice = await Add(household, "米");
        var salt = await Add(household, "鹽");
        var initial = await member.GetFromJsonAsync<JsonElement>(household.Path);
        Assert.All(initial.EnumerateArray(),
            item => Assert.False(item.GetProperty("completed").GetBoolean()));

        var results = await Task.WhenAll(
            household.Owner.PutAsJsonAsync(household.Item(rice.Id), new { name = "米", completed = true, rice.Version }),
            member.PutAsJsonAsync(household.Item(salt.Id), new { name = "海鹽", completed = true, salt.Version }));
        foreach (var result in results)
        {
            using (result)
                Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        }

        var list = await member.GetFromJsonAsync<JsonElement>(household.Path);
        Assert.Collection(list.EnumerateArray(),
            item =>
            {
                Assert.Equal("米", item.GetProperty("name").GetString());
                Assert.True(item.GetProperty("completed").GetBoolean());
                Assert.NotEqual(rice.Version, item.GetProperty("version").GetInt64());
            },
            item =>
            {
                Assert.Equal("海鹽", item.GetProperty("name").GetString());
                Assert.True(item.GetProperty("completed").GetBoolean());
                Assert.NotEqual(salt.Version, item.GetProperty("version").GetInt64());
            });
    }

    [Fact]
    public async Task OnlyOneConcurrentEditSucceedsAndTheConflictCanBeReloaded()
    {
        await using var household = await ShoppingHousehold.Create();
        using var member = await household.Join("member");
        var rice = await Add(household, "米");
        var path = household.Item(rice.Id);
        var submissions = new[] { "糙米", "白米" };
        var results = await Task.WhenAll(
            household.Owner.PutAsJsonAsync(path, new { name = submissions[0], completed = true, rice.Version }),
            member.PutAsJsonAsync(path, new { name = submissions[1], completed = true, rice.Version }));
        using var winner = Assert.Single(results, response => response.StatusCode == HttpStatusCode.OK);
        using var conflict = Assert.Single(results, response => response.StatusCode == HttpStatusCode.Conflict);
        var winningName = submissions[Array.IndexOf(results, winner)];
        var staleUser = winner == results[0] ? member : household.Owner;
        var current = Assert.Single((await staleUser.GetFromJsonAsync<JsonElement>(household.Path)).EnumerateArray());
        Assert.Equal(winningName, current.GetProperty("name").GetString());
        Assert.True(current.GetProperty("completed").GetBoolean());

        using var retried = await staleUser.PutAsJsonAsync(path, new
        {
            name = winningName,
            completed = false,
            version = current.GetProperty("version").GetInt64(),
        });
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.False((await retried.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("completed").GetBoolean());
    }

    [Fact]
    public async Task DeletingRequiresTheCurrentVersionAndNeverReassignsAnItemId()
    {
        await using var household = await ShoppingHousehold.Create();
        var item = await Add(household, "米");
        var path = household.Item(item.Id);
        using var changed = await household.Owner.PutAsJsonAsync(path, new { name = "糙米", completed = true, item.Version });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var latest = (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();

        using var stale = await household.Owner.DeleteAsync($"{path}&version={item.Version}");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Single((await household.Owner.GetFromJsonAsync<JsonElement>(household.Path)).EnumerateArray());

        using var deleted = await household.Owner.DeleteAsync($"{path}&version={latest}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await household.Owner.GetFromJsonAsync<JsonElement>(household.Path)).EnumerateArray());
        using var replacement = await household.Owner.PostAsJsonAsync(household.Path, new
        {
            id = item.Id, name = "白米", completed = false,
        });
        Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
        Assert.NotEqual(item.Id, (await replacement.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());
        using var absent = await household.Owner.DeleteAsync($"{path}&version={latest}");
        Assert.Equal(HttpStatusCode.Conflict, absent.StatusCode);
    }

    [Fact]
    public async Task AnOutsiderCannotReadOrWriteAnotherHouseholdsShopping()
    {
        await using var household = await ShoppingHousehold.Create();
        using var outsider = household.Browser();
        using var anonymous = household.Browser();
        await household.SignIn(outsider, "outsider");
        using var created = await outsider.PostAsJsonAsync("/api/households", new { name = "Cottage" });
        created.EnsureSuccessStatusCode();
        var otherId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var otherList = $"/api/households/{otherId}/shopping";
        var rice = await Add(household, "米");
        var update = new { name = "白米", completed = true, rice.Version };
        var item = household.Item(rice.Id);

        using var noSession = await anonymous.GetAsync(household.Path);
        Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
        using var read = await outsider.GetAsync(household.Path);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        using var add = await outsider.PostAsJsonAsync(household.Path, update);
        Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);
        using var change = await outsider.PutAsJsonAsync(item, update);
        Assert.Equal(HttpStatusCode.Forbidden, change.StatusCode);
        using var remove = await outsider.DeleteAsync($"{item}&version={rice.Version}");
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
        using var wrongHousehold = await outsider.PutAsJsonAsync($"{otherList}/item?id={rice.Id}", update);
        Assert.Equal(HttpStatusCode.Conflict, wrongHousehold.StatusCode);
        Assert.Empty((await outsider.GetFromJsonAsync<JsonElement>(otherList)).EnumerateArray());

        using var wholeList = await household.Owner.PutAsJsonAsync(household.Path, Array.Empty<object>());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wholeList.StatusCode);
        var remaining = Assert.Single((await household.Owner.GetFromJsonAsync<JsonElement>(household.Path)).EnumerateArray());
        Assert.Equal("米", remaining.GetProperty("name").GetString());
    }

    [Fact]
    public async Task InvalidShoppingWritesDoNotAlterTheList()
    {
        await using var household = await ShoppingHousehold.Create();
        var rice = await Add(household, "米");
        var path = household.Item(rice.Id);
        object[] invalid = [
            new { completed = false },
            new { name = " ", completed = false },
            new { name = new string('米', 101), completed = false },
            new { name = "米\0", completed = false },
            new { name = "米" },
        ];
        foreach (var body in invalid)
        {
            using var add = await household.Owner.PostAsJsonAsync(household.Path, body);
            Assert.Equal(HttpStatusCode.BadRequest, add.StatusCode);
            var updateBody = JsonSerializer.SerializeToNode(body)!.AsObject();
            updateBody["version"] = rice.Version;
            using var update = await household.Owner.PutAsJsonAsync(path, updateBody);
            Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        }

        var validItem = new { name = "白米", completed = true, rice.Version };
        using var blankId = await household.Owner.PutAsJsonAsync(household.Item(" "), validItem);
        Assert.Equal(HttpStatusCode.BadRequest, blankId.StatusCode);
        using var nulId = await household.Owner.PutAsJsonAsync(household.Item("rice\0"), validItem);
        Assert.Equal(HttpStatusCode.BadRequest, nulId.StatusCode);
        using var invalidVersion = await household.Owner.PutAsJsonAsync(path, new { name = "白米", completed = true, version = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalidVersion.StatusCode);
        using var invalidRemoval = await household.Owner.DeleteAsync($"{path}&version=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalidRemoval.StatusCode);
        using var blankRemoval = await household.Owner.DeleteAsync($"{household.Item(" ")}&version={rice.Version}");
        Assert.Equal(HttpStatusCode.BadRequest, blankRemoval.StatusCode);
        var remaining = Assert.Single((await household.Owner.GetFromJsonAsync<JsonElement>(household.Path)).EnumerateArray());
        Assert.Equal(rice.Version, remaining.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task MigratedItemsKeepTheirIdsAndAdditionalDataWhenUpdated()
    {
        await using var household = await ShoppingHousehold.Create();
        const string id = "../legacy/米&%2F";
        var path = await household.ClaimLegacy(
            new { id, name = "米", marker = new { shelf = 2 } },
            new { id = "legacy-oats", name = "燕麥", completed = 1 });
        var migrated = await household.Owner.GetFromJsonAsync<JsonElement>(path);
        var item = Assert.Single(migrated.EnumerateArray(), item => item.GetProperty("id").GetString() == id);
        Assert.True(Assert.Single(migrated.EnumerateArray(), item => item.GetProperty("id").GetString() == "legacy-oats")
            .GetProperty("completed").GetBoolean());
        Assert.Equal(id, item.GetProperty("id").GetString());
        Assert.False(item.GetProperty("completed").GetBoolean());

        using var changed = await household.Owner.PutAsJsonAsync($"{path}/item?id={Uri.EscapeDataString(id)}", new
        {
            name = "糙米", completed = true, version = item.GetProperty("version").GetInt64(),
        });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var latest = await household.Owner.GetFromJsonAsync<JsonElement>(path);
        var updated = Assert.Single(latest.EnumerateArray(), item => item.GetProperty("id").GetString() == id);
        Assert.Equal(id, updated.GetProperty("id").GetString());
        Assert.Equal("糙米", updated.GetProperty("name").GetString());
        Assert.True(updated.GetProperty("completed").GetBoolean());
        Assert.Equal(2, updated.GetProperty("marker").GetProperty("shelf").GetInt32());
    }

    private static async Task<(string Id, long Version)> Add(ShoppingHousehold household, string name)
    {
        using var response = await household.Owner.PostAsJsonAsync(household.Path, new { name, completed = false });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (item.GetProperty("id").GetString()!, item.GetProperty("version").GetInt64());
    }

    private sealed class ShoppingHousehold : IAsyncDisposable
    {
        private readonly PostgresTestDatabase database = new();
        private readonly GoogleTestIdentityProvider google = new();
        private WebApplicationFactory<Program> phaseOne = null!;
        private WebApplicationFactory<Program> factory = null!;

        public HttpClient Owner { get; private set; } = null!;
        public Guid Id { get; private set; }
        public string Path => $"/api/households/{Id}/shopping";

        public static async Task<ShoppingHousehold> Create()
        {
            var setup = new ShoppingHousehold();
            await setup.database.Initialize();
            setup.phaseOne = setup.database.CreateFactory();
            setup.factory = setup.google.Configure(setup.phaseOne);
            setup.Owner = setup.Browser();
            await setup.SignIn(setup.Owner, "owner");
            using var created = await setup.Owner.PostAsJsonAsync("/api/households", new { name = "Home" });
            created.EnsureSuccessStatusCode();
            setup.Id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            return setup;
        }

        public async Task<string> ClaimLegacy(params object[] items)
        {
            using var paired = phaseOne.CreateClient();
            paired.DefaultRequestHeaders.Add("x-household-code", "test-code");
            var scopedPath = $"/api/households/{PostgresStore.LegacyHouseholdId}/shopping";
            using var phaseOneScoped = await paired.GetAsync(scopedPath);
            Assert.Equal(HttpStatusCode.NotFound, phaseOneScoped.StatusCode);
            using var set = await paired.PutAsJsonAsync("/api/shopping", items);
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
            using var claimed = await Owner.PostAsync("/api/households/legacy/claim", null);
            Assert.Equal(HttpStatusCode.Created, claimed.StatusCode);
            return scopedPath;
        }

        public string Item(string id) => $"{Path}/item?id={Uri.EscapeDataString(id)}";

        public async Task<HttpClient> Join(string subject)
        {
            var client = Browser();
            var user = await SignIn(client, subject);
            // The invitation API is a separate issue. Seed this membership and exercise shopping only over HTTP.
            await using var connection = new NpgsqlConnection(database.IsolatedConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                INSERT INTO household_members (household_id, user_id, role)
                VALUES (@household, @user, 'member');
                """, connection);
            command.Parameters.AddWithValue("household", Id);
            command.Parameters.AddWithValue("user", user);
            await command.ExecuteNonQueryAsync();
            return client;
        }

        public HttpClient Browser() => factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new("https://localhost"),
        });

        public async Task<Guid> SignIn(HttpClient client, string subject)
        {
            using var response = await client.PostAsJsonAsync("/api/auth/google", new
            {
                credential = google.Credential(subject, $"{subject}@gmail.com"),
            });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetGuid();
        }

        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            await factory.DisposeAsync();
            await phaseOne.DisposeAsync();
            google.Dispose();
            await database.DisposeAsync();
        }
    }
}
