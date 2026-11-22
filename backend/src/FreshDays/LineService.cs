using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FreshDays;

public record LineConfig(string Code, string Secret, string Token, bool PhaseTwo);

public sealed class LineService(PostgresStore store, IHttpClientFactory clients, LineConfig config, ILogger<LineService> logger, LineHouseholdStore households)
{
    private static string? String(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static JsonElement Child(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value : default;

    public async Task Handle(JsonElement e, CancellationToken ct)
    {
        var type = String(e, "type");
        var replyToken = String(e, "replyToken");
        var userId = String(Child(e, "source"), "userId");
        var message = Child(e, "message");
        var text = String(message, "type") == "text" ? String(message, "text")?.Trim() : null;
        if (config.PhaseTwo)
        {
            await HandleHousehold(type, Child(e, "source"), userId, replyToken, text, ct);
            return;
        }

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

    private const string Rebind = "請登入鮮日子，選擇家庭並重新綁定 LINE，才能查詢到期食材與接收提醒。";
    private const string NoDue = "目前沒有三天內到期的食材。";

    private async Task HandleHousehold(string? type, JsonElement source, string? userId, string? replyToken,
        string? text, CancellationToken ct)
    {
        // A reply is visible to every participant in a group chat.
        if (String(source, "type") != "user" || string.IsNullOrEmpty(userId))
            return;

        if (type == "unfollow")
        {
            await households.Unbind(userId, ct);
            return;
        }

        if (replyToken is null)
            return;

        if (type == "follow")
        {
            await Reply(replyToken, Rebind, ct);
            return;
        }
        if (type != "message" || text is null)
            return;

        if (text.StartsWith("綁定 ", StringComparison.Ordinal))
        {
            var bound = await households.Bind(userId, text[3..].Trim(), ct);
            await Reply(replyToken, bound ? "LINE 綁定完成。傳送「到期」可查詢，傳送「取消訂閱」可停止提醒。" :
                "綁定指令無效或已失效。請回鮮日子產生新的指令。", ct);
        }
        else if (text == "取消訂閱")
        {
            await households.Unbind(userId, ct);
            await Reply(replyToken, "已取消訂閱到期提醒。", ct);
        }
        else if (text == "到期")
        {
            var binding = await households.Find(userId, ct);
            if (binding is null || !await households.UseBinding(binding, async transaction =>
            {
                var inventory = await new InventoryStore(transaction, binding.HouseholdId, ct).Read();
                await Reply(replyToken, DueMessage(inventory, DateTimeOffset.UtcNow) ?? NoDue, ct);
            }, ct))
                await Reply(replyToken, Rebind, ct);
        }
        else if (text.Equals("ping", StringComparison.OrdinalIgnoreCase))
            await Reply(replyToken, "pong 🥬 鮮日子 LINE Webhook 已連線。", ct);
    }

    private async Task Reply(string replyToken, string text, CancellationToken ct)
    {
        try { await Send("reply", new { replyToken, messages = new[] { new { type = "text", text } } }, null, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "LINE reply failed"); }
    }

    public static DateTimeOffset Taipei(DateTimeOffset utc) => utc.ToOffset(TimeSpan.FromHours(8));
    public async Task<string?> DueMessage(DateTimeOffset now) =>
        DueMessage(await store.ReadList("inventory"), now);

    private static string? DueMessage(string inventory, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(inventory);
        var items = doc.RootElement.EnumerateArray().Select(x => new FoodItem(x.GetProperty("id").GetString()!, x.GetProperty("name").GetString()!, String(x, "expiresAt"), String(x, "status")));
        return Expiry.Message(items, DateOnly.FromDateTime(Taipei(now).DateTime));
    }
    public async Task PushDue(DateTimeOffset now, CancellationToken ct = default)
    {
        var local = Taipei(now);
        if (local.Hour < 9) return;
        if (config.PhaseTwo)
        {
            await PushHouseholdDue(local, ct);
            return;
        }
        var message = await DueMessage(now);
        if (message is null) return;
        var day = DateOnly.FromDateTime(local.DateTime);
        foreach (var user in await store.Subscribers())
        {
            var (key, sent) = await store.Notification(day, user);
            if (sent) continue;
            try
            {
                await Send("push", new { to = user, messages = new[] { new { type = "text", text = message } } }, key, ct);
                await store.MarkSent(day, user);
            }
            catch (Exception ex) { logger.LogWarning(ex, "LINE push failed; will retry"); }
        }
    }
    private async Task PushHouseholdDue(DateTimeOffset local, CancellationToken ct)
    {
        var day = DateOnly.FromDateTime(local.DateTime);
        foreach (var binding in await households.ReminderCandidates(day, ct))
        {
            // Persist the exact payload before calling LINE. A restarted worker
            // retries the same request even if the inventory has since changed.
            if (!await households.UseBinding(binding, async transaction =>
            {
                var inventory = await new InventoryStore(transaction, binding.HouseholdId, ct).Read();
                var message = DueMessage(inventory, local);
                if (message is not null)
                    await LineHouseholdStore.QueueReminder(transaction, binding, day, message, ct);
            }, ct))
                continue;

            try
            {
                await households.UseBinding(binding, async transaction =>
                {
                    var message = await LineHouseholdStore.PendingReminder(transaction, binding, day, ct);
                    if (message is null)
                        return;

                    // The date and LINE account keep the key stable even if a
                    // membership cascade removes its notification after acceptance.
                    var identity = FormattableString.Invariant($"freshdays-household:{day.DayNumber}:{binding.LineUserId}");
                    var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
                    var key = new Guid(digest.AsSpan(0, 16)).ToString();
                    await Send("push", new { to = binding.LineUserId, messages = new[] { new { type = "text", text = message } } }, key, ct);
                    await LineHouseholdStore.MarkReminderSent(transaction, binding, day, ct);
                }, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "LINE household push failed; will retry");
            }
        }
    }

    private async Task Send(string endpoint, object body, string? retryKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.line.me/v2/bot/message/{endpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        if (retryKey is not null) request.Headers.Add("X-Line-Retry-Key", retryKey);
        request.Content = JsonContent.Create(body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = clients.CreateClient("LineMessaging");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (retryKey is not null && response.StatusCode == HttpStatusCode.Conflict &&
            (!config.PhaseTwo || response.Headers.Contains("x-line-accepted-request-id")))
            return;
        response.EnsureSuccessStatusCode();
    }
}

public sealed class ExpiryWorker(LineService line, IConfiguration config, TimeProvider clock, ILogger<ExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config.GetValue<bool>("DisableWorker")) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        while (true)
        {
            try { await line.PushDue(clock.GetUtcNow(), stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Expiry notification check failed"); }
            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }
}
