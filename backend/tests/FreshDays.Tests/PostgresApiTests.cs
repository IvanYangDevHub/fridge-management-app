using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FreshDays.Tests;

public class PostgresApiTests
{
    [Fact]
    public async Task ReplacingListsPreservesRowsAndOrderAfterRestart()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using (var firstHost = database.CreateFactory())
        using (var firstClient = firstHost.CreateClient())
        {
            firstClient.DefaultRequestHeaders.Add("x-household-code", "test-code");
            Assert.Empty(await firstClient.GetFromJsonAsync<JsonElement[]>("/api/inventory") ?? []);
            var food = """[{"id":"z","name":"牛奶","expiresAt":null,"custom":{"note":"留著"}},{"id":"a","name":"雞蛋"}]""";
            var shopping = """[{"id":"z","name":"鹽","completed":false},{"id":"b","name":"米"}]""";
            Assert.Equal(HttpStatusCode.NoContent, (await firstClient.PutAsJsonAsync("/api/INVENTORY", JsonDocument.Parse(food).RootElement)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await firstClient.PostAsJsonAsync("/api/shopping", JsonDocument.Parse(shopping).RootElement)).StatusCode);
        }

        await using var secondHost = database.CreateFactory();
        using var secondClient = secondHost.CreateClient();
        secondClient.DefaultRequestHeaders.Add("x-household-code", "test-code");
        var inventory = await secondClient.GetFromJsonAsync<JsonElement>("/api/inventory");
        Assert.Equal(new[] { "z", "a" }, inventory.EnumerateArray().Select(row => row.GetProperty("id").GetString()));
        Assert.Equal(JsonValueKind.Null, inventory[0].GetProperty("expiresAt").ValueKind);
        Assert.False(inventory[1].TryGetProperty("expiresAt", out _));
        Assert.Equal("留著", inventory[0].GetProperty("custom").GetProperty("note").GetString());
        var shoppingList = await secondClient.GetFromJsonAsync<JsonElement>("/api/shopping");
        Assert.Equal(new[] { "z", "b" }, shoppingList.EnumerateArray().Select(row => row.GetProperty("id").GetString()));
        Assert.False(shoppingList[0].GetProperty("completed").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await secondClient.PutAsJsonAsync("/api/inventory", Array.Empty<object>())).StatusCode);
        Assert.Empty(await secondClient.GetFromJsonAsync<JsonElement[]>("/api/inventory") ?? []);
        Assert.Equal(2, (await secondClient.GetFromJsonAsync<JsonElement>("/api/shopping")).GetArrayLength());
    }

    [Theory]
    [InlineData("""[{"id":"same","name":"一"},{"id":"same","name":"二"}]""")]
    [InlineData("""[{"id":"ok","name":"一","extra":{"note":"\u0000"}}]""")]
    public async Task InvalidReplacementPreservesPreviouslySavedRows(string invalid)
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var host = database.CreateFactory();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("x-household-code", "test-code");
        using var original = new StringContent("""[{"id":"saved","name":"鹽"}]""");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync("/api/shopping", original)).StatusCode);
        using var replacement = new StringContent(invalid);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("/api/shopping", replacement)).StatusCode);
        var items = await client.GetFromJsonAsync<JsonElement[]>("/api/shopping");
        Assert.Equal("saved", Assert.Single(items!).GetProperty("id").GetString());
    }

    [Fact]
    public async Task StartupRequiresPostgresConfiguration()
    {
        await using var database = new PostgresTestDatabase();
        await using var host = database.CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:FreshDays"] = null })));
        var error = Assert.Throws<InvalidOperationException>(() => host.CreateClient());
        Assert.Equal("ConnectionStrings__FreshDays is required.", error.Message);
    }

    [Fact]
    public async Task ConcurrentReplacementsLeaveOneCompleteList()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var host = database.CreateFactory();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("x-household-code", "test-code");
        var first = Enumerable.Range(1, 40).Select(n => new { id = $"a{n}", name = "雞蛋" }).ToArray();
        var second = Enumerable.Range(1, 40).Select(n => new { id = $"b{n}", name = "牛奶" }).ToArray();

        var responses = await Task.WhenAll(client.PutAsJsonAsync("/api/inventory", first), client.PutAsJsonAsync("/api/inventory", second));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
        foreach (var response in responses)
            response.Dispose();

        var items = await client.GetFromJsonAsync<JsonElement[]>("/api/inventory");
        var ids = items!.Select(row => row.GetProperty("id").GetString()).ToArray();
        Assert.True(ids.SequenceEqual(first.Select(row => row.id)) || ids.SequenceEqual(second.Select(row => row.id)));
    }
}
