using System.Net;
using System.Text.Json;
using FreshDays;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FreshDays.Tests;

public class LineServiceTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public List<(string Path, string Body, string? Retry)> Calls { get; } = [];
        public bool FailNext { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync(cancellationToken), request.Headers.TryGetValues("X-Line-Retry-Key", out var values) ? values.Single() : null));
            if (FailNext) { FailNext = false; return new(HttpStatusCode.ServiceUnavailable); }
            return new(HttpStatusCode.OK);
        }
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }
    private static JsonElement Event(string text, string? user = "U1", string? reply = "reply") => JsonSerializer.SerializeToElement(new { type = "message", source = new { userId = user }, replyToken = reply, message = new { type = "text", text } });
    private static string Text(string body) => JsonDocument.Parse(body).RootElement.GetProperty("messages")[0].GetProperty("text").GetString()!;

    [Fact]
    public async Task SubscriptionQueryAndPushRetryUsePersistentState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"freshdays-line-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteStore(path);
            await store.Initialize();
            var recorder = new Recorder();
            var line = new LineService(store, new Factory(recorder), new("code", "secret", "token"), NullLogger<LineService>.Instance);
            await line.Handle(Event("訂閱 wrong"));
            Assert.Empty(await store.Subscribers());
            Assert.Contains("配對碼不正確", Text(recorder.Calls[^1].Body));
            await line.Handle(Event("訂閱 code"));
            Assert.Equal(new[] { "U1" }, await store.Subscribers());
            await line.Handle(Event("到期"));
            Assert.Contains("目前沒有三天內到期的食材", Text(recorder.Calls[^1].Body));
            await store.ReplaceInventory("[{\"id\":\"1\",\"name\":\"牛奶\",\"expiresAt\":\"2026-11-08\",\"status\":\"active\"}]");
            var now = new DateTimeOffset(2026, 11, 7, 2, 0, 0, TimeSpan.Zero);
            await line.PushDue(now.AddHours(-2));
            Assert.DoesNotContain(recorder.Calls, call => call.Path.EndsWith("/push"));
            recorder.FailNext = true;
            await line.PushDue(now);
            await line.PushDue(now);
            await line.PushDue(now);
            var pushes = recorder.Calls.Where(call => call.Path.EndsWith("/push")).ToArray();
            Assert.Equal(2, pushes.Length);
            Assert.NotNull(pushes[0].Retry);
            Assert.Equal(pushes[0].Retry, pushes[1].Retry);
            Assert.Contains("牛奶", JsonDocument.Parse(pushes[1].Body).RootElement.GetProperty("messages")[0].GetProperty("text").GetString());
            await line.Handle(Event("取消訂閱"));
            Assert.Empty(await store.Subscribers());
            await line.Handle(Event("到期"));
            Assert.Contains("請先傳送", Text(recorder.Calls[^1].Body));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
