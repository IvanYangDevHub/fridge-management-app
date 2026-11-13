using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreshDays;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["ASPNETCORE_URLS"]))
    builder.WebHost.UseUrls(builder.Configuration["PORT"] is { Length: > 0 } port ? $"http://0.0.0.0:{port}" : "http://localhost:3000");
builder.Services.AddSingleton(sp => new SqliteStore(sp.GetRequiredService<IConfiguration>()["DatabasePath"] ?? sp.GetRequiredService<IConfiguration>()["SQLITE_PATH"] ?? "freshdays.db"));
builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp => {
    var config = sp.GetRequiredService<IConfiguration>();
    var code = config["HouseholdCode"] ?? config["HOUSEHOLD_CODE"] ?? "";
    var secret = config["Line:ChannelSecret"] ?? config["LINE_CHANNEL_SECRET"] ?? "";
    var token = config["Line:AccessToken"] ?? config["LINE_CHANNEL_ACCESS_TOKEN"] ?? "";
    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("HOUSEHOLD_CODE, LINE_CHANNEL_SECRET and LINE_CHANNEL_ACCESS_TOKEN are required.");
    return new LineConfig(code, secret, token);
});
builder.Services.AddSingleton<LineService>();
builder.Services.AddHostedService<ExpiryWorker>();
var app = builder.Build();
var lineConfig = app.Services.GetRequiredService<LineConfig>();
var code = lineConfig.Code;
var secret = lineConfig.Secret;
await app.Services.GetRequiredService<SqliteStore>().Initialize();

app.MapGet("/health", () => Results.Text("ok"));
app.MapMethods("/api/{list:regex(^(inventory|shopping)$)}", ["GET", "PUT", "POST"], async (HttpContext context, string list, SqliteStore store) =>
{
    var provided = context.Request.Headers["x-household-code"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(code))) return Results.Unauthorized();
    context.Response.Headers.CacheControl = "no-store";
    if (HttpMethods.IsGet(context.Request.Method)) return Results.Text(await store.ReadList(list), "application/json");
    if (context.Request.ContentLength > 1_048_576) return Results.StatusCode(413);
    try
    {
        using var body = await JsonDocument.ParseAsync(context.Request.Body);
        if (body.RootElement.ValueKind != JsonValueKind.Array || body.RootElement.GetArrayLength() > 500) return Results.BadRequest();
        foreach (var item in body.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) || !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Length > 100) return Results.BadRequest();
            if (list == "inventory" && item.TryGetProperty("expiresAt", out var date) && date.ValueKind != JsonValueKind.Null && (date.ValueKind != JsonValueKind.String || !DateOnly.TryParseExact(date.GetString(), "yyyy-MM-dd", out _))) return Results.BadRequest();
        }
        await store.ReplaceList(list, body.RootElement.GetRawText());
        return Results.NoContent();
    }
    catch (JsonException) { return Results.BadRequest(); }
});

app.MapPost("/webhook/line", async (HttpContext context, LineService line) =>
{
    if (context.Request.ContentLength > 1_048_576) return Results.StatusCode(413);
    using var stream = new MemoryStream();
    await context.Request.Body.CopyToAsync(stream);
    if (stream.Length > 1_048_576) return Results.StatusCode(413);
    var body = stream.ToArray();
    if (!LineSignature.Verify(body, context.Request.Headers["x-line-signature"].ToString(), secret)) return Results.Unauthorized();
    try
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) return Results.BadRequest();
        foreach (var item in events.EnumerateArray()) await line.Handle(item);
        return Results.Ok();
    }
    catch (JsonException) { return Results.BadRequest(); }
});
app.Run();
public partial class Program;
