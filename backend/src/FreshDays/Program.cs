using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreshDays;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["ASPNETCORE_URLS"]))
    builder.WebHost.UseUrls(builder.Configuration["PORT"] is { Length: > 0 } port ? $"http://0.0.0.0:{port}" : "http://localhost:3000");
builder.Services.AddSingleton(sp => NpgsqlDataSource.Create(
    sp.GetRequiredService<IConfiguration>().GetConnectionString("FreshDays")
    ?? throw new InvalidOperationException("ConnectionStrings__FreshDays is required.")));
builder.Services.AddSingleton<PostgresStore>();
builder.Services.AddSingleton<HouseholdStore>();
builder.Services.AddSingleton<InvitationStore>();
builder.Services.AddHttpClient();
builder.Services.AddAppSessions();
builder.Services.AddSingleton(sp => {
    var config = sp.GetRequiredService<IConfiguration>();
    var code = config["HouseholdCode"] ?? config["HOUSEHOLD_CODE"] ?? "";
    var secret = config["Line:ChannelSecret"] ?? config["LINE_CHANNEL_SECRET"] ?? "";
    var token = config["Line:AccessToken"] ?? config["LINE_CHANNEL_ACCESS_TOKEN"] ?? "";
    if (!config.GetValue<bool>("Product:PhaseTwo") && string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("HOUSEHOLD_CODE is required in phase one.");
    if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("LINE_CHANNEL_SECRET and LINE_CHANNEL_ACCESS_TOKEN are required.");
    return new LineConfig(code, secret, token);
});
builder.Services.AddSingleton<LineService>();
builder.Services.AddHostedService<ExpiryWorker>();
var app = builder.Build();
var phaseTwo = app.Configuration.GetValue<bool>("Product:PhaseTwo");
await PostgresMigrations.Apply(app.Services.GetRequiredService<NpgsqlDataSource>());
var lineConfig = app.Services.GetRequiredService<LineConfig>();
var code = lineConfig.Code;
var secret = lineConfig.Secret;

app.MapGet("/health", () => Results.Text("ok"));
app.MapAppSessions(phaseTwo);
app.MapHouseholds(phaseTwo);
app.MapInvitations(phaseTwo);

app.MapMethods("/api/{list:regex(^(inventory|shopping)$)}", ["GET", "PUT", "POST"], async (HttpContext context, string list, PostgresStore store) =>
{
    if (phaseTwo) return Results.NotFound();
    var provided = context.Request.Headers["x-household-code"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(code))) return Results.Unauthorized();
    list = list.ToLowerInvariant();
    context.Response.Headers.CacheControl = "no-store";
    if (HttpMethods.IsGet(context.Request.Method)) return Results.Text(await store.ReadList(list), "application/json");
    return await ListPayload.Replace(context, list, json => store.ReplaceList(list, json));
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
        if (!phaseTwo)
            foreach (var item in events.EnumerateArray()) await line.Handle(item);
        return Results.Ok();
    }
    catch (JsonException) { return Results.BadRequest(); }
});
app.Run();
public partial class Program;
