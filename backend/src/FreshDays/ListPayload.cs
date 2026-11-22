using System.Text.Json;

namespace FreshDays;

internal static class ListPayload
{
    public static async Task<IResult> Replace(HttpContext context, string list, Func<string, Task<bool>> save)
    {
        if (context.Request.ContentLength > 1_048_576) return Results.StatusCode(413);
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (body.RootElement.ValueKind != JsonValueKind.Array || body.RootElement.GetArrayLength() > 500) return Results.BadRequest();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in body.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) || !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Length > 100) return Results.BadRequest();
                if (!ids.Add(id.GetString()!)) return Results.BadRequest();
                if (list == "inventory" && item.TryGetProperty("expiresAt", out var date) && date.ValueKind != JsonValueKind.Null && (date.ValueKind != JsonValueKind.String || !DateOnly.TryParseExact(date.GetString(), "yyyy-MM-dd", out _))) return Results.BadRequest();
            }
            if (!await save(body.RootElement.GetRawText())) return Results.BadRequest();
            return Results.NoContent();
        }
        catch (JsonException) { return Results.BadRequest(); }
    }
}
