namespace FreshDays;

public record FoodItem(string Id, string Name, string? ExpiresAt, string? Status);

public static class Expiry
{
    public static IReadOnlyList<FoodItem> Due(IEnumerable<FoodItem> items, DateOnly today) => items
        .Where(x => x.Status != "consumed" && DateOnly.TryParseExact(x.ExpiresAt, "yyyy-MM-dd", out var date) && date <= today.AddDays(3))
        .OrderBy(x => x.ExpiresAt, StringComparer.Ordinal).ToArray();

    public static string? Message(IEnumerable<FoodItem> items, DateOnly today)
    {
        var due = Due(items, today);
        if (due.Count == 0) return null;
        var lines = due.Take(15).Select(item =>
        {
            var date = DateOnly.ParseExact(item.ExpiresAt!, "yyyy-MM-dd");
            var label = date < today ? "已過期" : date == today ? "今天到期" : $"{item.ExpiresAt} 到期";
            return $"・{item.Name}：{label}";
        });
        return $"【鮮日子｜{today:yyyy-MM-dd} 到期提醒】\n需處理 {due.Count} 項食材：\n{string.Join("\n", lines)}{(due.Count > 15 ? "\n・其餘請查看冰箱清單" : "")}\n請確認實際保存狀態。";
    }
}
