using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FreshDays.Tests;

public class LineServiceTests
{
    private sealed class Recorder : HttpMessageHandler, IHttpClientFactory
    {
        public List<(string Endpoint, string Text, string? RetryKey)> Calls { get; } = [];
        public Queue<HttpStatusCode> PushResults { get; } = new();

        public HttpClient CreateClient(string name) => new(this, false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var endpoint = request.RequestUri!.Segments[^1];
            var text = body.RootElement.GetProperty("messages")[0].GetProperty("text").GetString()!;
            var retryKey = request.Headers.TryGetValues("X-Line-Retry-Key", out var keys) ? keys.Single() : null;
            Calls.Add((endpoint, text, retryKey));
            var status = endpoint == "push" ? PushResults.Dequeue() : HttpStatusCode.OK;
            return new HttpResponseMessage(status);
        }
    }

    private static async Task SendWebhook(HttpClient client, string text)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            events = new[]
            {
                new { type = "message", source = new { userId = "U1" }, replyToken = "reply", message = new { type = "text", text } }
            }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhook/line")
        {
            Content = new ByteArrayContent(body)
        };
        request.Headers.Add("x-line-signature", Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), body)));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SignedWebhookCannotSubscribeWithTheOldCodeAfterTheSecondCutover()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        using var recorder = new Recorder();
        await using var phaseOne = database.CreateFactory(recorder);
        await using var phaseTwo = phaseOne.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, settings) =>
            settings.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Product:PhaseTwo"] = "true",
                ["Auth:GoogleClientId"] = "freshdays-test-client",
            })));

        using var phaseTwoClient = phaseTwo.CreateClient();
        await SendWebhook(phaseTwoClient, "訂閱 test-code");
        Assert.Empty(recorder.Calls);

        using var phaseOneClient = phaseOne.CreateClient();
        await SendWebhook(phaseOneClient, "到期");
        Assert.Contains("請先傳送", Assert.Single(recorder.Calls).Text);
    }
    [Fact]
    public async Task SubscriptionAndRetryStateSurviveRestarts()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        using var recorder = new Recorder();
        var localDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        var now = new DateTimeOffset(localDate.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(8));
        using (var firstHost = database.CreateFactory(recorder))
        using (var firstClient = firstHost.CreateClient())
        {
            await SendWebhook(firstClient, "訂閱 wrong");
            Assert.Contains("配對碼不正確", recorder.Calls[^1].Text);
            await SendWebhook(firstClient, "到期");
            Assert.Contains("請先傳送", recorder.Calls[^1].Text);
            await SendWebhook(firstClient, "訂閱 test-code");
            await SendWebhook(firstClient, "訂閱 test-code");
            await SendWebhook(firstClient, "到期");
            Assert.Contains("目前沒有三天內到期的食材", recorder.Calls[^1].Text);
            var reminders = firstHost.Services.GetRequiredService<LineService>();
            await reminders.PushDue(now);
            Assert.DoesNotContain(recorder.Calls, call => call.Endpoint == "push");
            firstClient.DefaultRequestHeaders.Add("x-household-code", "test-code");
            var food = new[] { new { id = "one", name = "牛奶", expiresAt = localDate.AddDays(1).ToString("yyyy-MM-dd"), status = "active" } };
            Assert.Equal(HttpStatusCode.NoContent, (await firstClient.PutAsJsonAsync("/api/inventory", food)).StatusCode);
            await reminders.PushDue(now.AddHours(-2));
            Assert.DoesNotContain(recorder.Calls, call => call.Endpoint == "push");
            recorder.PushResults.Enqueue(HttpStatusCode.ServiceUnavailable);
            await reminders.PushDue(now);
            Assert.Single(recorder.Calls, call => call.Endpoint == "push");
        }

        using (var secondHost = database.CreateFactory(recorder))
        using (var secondClient = secondHost.CreateClient())
        {
            await SendWebhook(secondClient, "到期");
            Assert.Contains("牛奶", recorder.Calls[^1].Text);
            recorder.PushResults.Enqueue(HttpStatusCode.Conflict);
            await secondHost.Services.GetRequiredService<LineService>().PushDue(now);
            var pushes = recorder.Calls.Where(call => call.Endpoint == "push").ToArray();
            Assert.Equal(2, pushes.Length);
            Assert.True(Guid.TryParse(pushes[0].RetryKey, out _));
            Assert.Equal(pushes[0].RetryKey, pushes[1].RetryKey);
            Assert.Contains("牛奶", pushes[1].Text);
        }

        using (var thirdHost = database.CreateFactory(recorder))
        using (var thirdClient = thirdHost.CreateClient())
        {
            var reminders = thirdHost.Services.GetRequiredService<LineService>();
            await reminders.PushDue(now);
            Assert.Equal(2, recorder.Calls.Count(call => call.Endpoint == "push"));
            await SendWebhook(thirdClient, "取消訂閱");
            await SendWebhook(thirdClient, "到期");
            Assert.Contains("請先傳送", recorder.Calls[^1].Text);
            await reminders.PushDue(now.AddDays(1));
            Assert.Equal(2, recorder.Calls.Count(call => call.Endpoint == "push"));
        }
    }
}
