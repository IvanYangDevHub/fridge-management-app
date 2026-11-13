using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FreshDays;

public record LineConfig(string Code, string Secret, string Token);

public sealed class LineService(SqliteStore store, IHttpClientFactory clients, LineConfig config, ILogger<LineService> logger)
{
    private static string? String(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static JsonElement Child(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value : default;

    public async Task Handle(JsonElement e)
    {
        var type = String(e, "type");
        var replyToken = String(e, "replyToken");
        var userId = String(Child(e, "source"), "userId");
        var message = Child(e, "message");
        var text = String(message, "type") == "text" ? String(message, "text")?.Trim() : null;
        string? reply = null;
        if (type == "follow") reply = "歡迎使用鮮日子！傳送「訂閱 配對碼」開啟每日到期提醒。";
        else if (type == "message" && text is not null)
        {
            if (text.Equals("ping", StringComparison.OrdinalIgnoreCase)) reply = "pong 🥬 鮮日子 LINE Webhook 已連線。";
            else if (text.StartsWith("訂閱 ", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(userId) || text[3..].Trim() != config.Code) reply = "配對碼不正確，請向家庭管理者確認。";
                else { await store.Subscribe(userId); reply = "已訂閱每日到期提醒。傳送「取消訂閱」可停止通知。"; }
            }
            else if (text == "取消訂閱" && userId is not null) { await store.Unsubscribe(userId); reply = "已取消訂閱到期提醒。"; }
            else if (text == "到期")
            {
                if (userId is null || !(await store.Subscribers()).Contains(userId)) reply = "請先傳送「訂閱 配對碼」。";
                else reply = await DueMessage(DateTimeOffset.UtcNow) ?? "目前沒有三天內到期的食材。";
            }
        }
        if (reply is not null && replyToken is not null)
        {
            try { await Send("reply", new { replyToken, messages = new[] { new { type = "text", text = reply } } }, null); }
            catch (Exception ex) { logger.LogWarning(ex, "LINE reply failed"); }
        }
    }

    public static DateTimeOffset Taipei(DateTimeOffset utc) => utc.ToOffset(TimeSpan.FromHours(8));
    public async Task<string?> DueMessage(DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(await store.ReadInventory());
        var items = doc.RootElement.EnumerateArray().Select(x => new FoodItem(String(x, "id") ?? "", String(x, "name") ?? "", String(x, "expiresAt"), String(x, "status")));
        return Expiry.Message(items, DateOnly.FromDateTime(Taipei(now).DateTime));
    }
    public async Task PushDue(DateTimeOffset now)
    {
        var local = Taipei(now);
        if (local.Hour < 9) return;
        var message = await DueMessage(now);
        if (message is null) return;
        var day = local.ToString("yyyy-MM-dd");
        foreach (var user in await store.Subscribers())
        {
            var (key, sent) = await store.Notification(day, user);
            if (sent) continue;
            try
            {
                await Send("push", new { to = user, messages = new[] { new { type = "text", text = message } } }, key);
                await store.MarkSent(day, user);
            }
            catch (Exception ex) { logger.LogWarning(ex, "LINE push failed; will retry"); }
        }
    }
    private async Task Send(string endpoint, object body, string? retryKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.line.me/v2/bot/message/{endpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        if (retryKey is not null) request.Headers.Add("X-Line-Retry-Key", retryKey);
        request.Content = JsonContent.Create(body);
        using var response = await clients.CreateClient().SendAsync(request);
        if (response.StatusCode != HttpStatusCode.Conflict || retryKey is null) response.EnsureSuccessStatusCode();
    }
}

public sealed class ExpiryWorker(LineService line, IConfiguration config, ILogger<ExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config.GetValue<bool>("DisableWorker")) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await line.PushDue(DateTimeOffset.UtcNow); }
            catch (Exception ex) { logger.LogError(ex, "Expiry notification check failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
