using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FreshDays.Tests;

public class ApiTests : IAsyncLifetime
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"freshdays-api-{Guid.NewGuid():N}.db");
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;

    public Task InitializeAsync()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> {
            ["DatabasePath"] = path, ["HouseholdCode"] = "test-code", ["Line:ChannelSecret"] = "secret", ["Line:AccessToken"] = "token", ["DisableWorker"] = "true"
        })));
        client = factory.CreateClient();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task InventoryAndShoppingRequireCodeAndPersistRoundTrip()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/inventory")).StatusCode);
        client.DefaultRequestHeaders.Add("x-household-code", "test-code");
        var food = new[] { new { id = "1", name = "牛奶", quantity = 1, unit = "瓶", location = "fridge", category = "dairy", expiresAt = "2026-11-08", status = "active" } };
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/inventory", food)).StatusCode);
        Assert.Equal("牛奶", (await client.GetFromJsonAsync<JsonElement>("/api/inventory"))[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/shopping", new[] { new { id = "2", name = "鹽", completed = false } })).StatusCode);
        Assert.Equal("鹽", (await client.GetFromJsonAsync<JsonElement>("/api/shopping"))[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/inventory", new[] { new { name = "missing id" } })).StatusCode);
    }

    [Fact]
    public async Task WebhookVerifiesRawBytesAndHandlesVerificationRequest()
    {
        async Task<HttpResponseMessage> Post(string body, bool signed)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/webhook/line") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (signed) request.Headers.Add("x-line-signature", Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes(body))));
            return await client.SendAsync(request);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("{\"events\":[]}", false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post("{\"events\":[]}", true)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("not-json", true)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("{}", true)).StatusCode);
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
        if (File.Exists(path)) File.Delete(path);
    }
}
