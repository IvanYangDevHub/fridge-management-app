using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FreshDays.Tests;

public partial class LineServiceTests
{
    [Fact]
    public async Task LegacySubscriberMustExplicitlyRebindToSeeOnlyTheSelectedHousehold()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        using var recorder = new Recorder();
        await using var phaseOne = database.CreateFactory(recorder);
        using var legacy = phaseOne.CreateClient();
        await SendWebhook(legacy, "訂閱 test-code");
        legacy.DefaultRequestHeaders.Add("x-household-code", "test-code");
        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        var expiresAt = date.AddDays(1).ToString("yyyy-MM-dd");
        (await legacy.PutAsJsonAsync("/api/inventory", new[] { new { id = "legacy", name = "舊家庭牛奶", expiresAt } }))
            .EnsureSuccessStatusCode();

        using var google = new GoogleTestIdentityProvider();
        await using var phaseTwo = google.Configure(phaseOne);
        using var browser = phaseTwo.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        using var authenticated = phaseTwo.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        (await authenticated.PostAsJsonAsync("/api/auth/google", new { credential = google.Credential("owner", "owner@gmail.com") }))
            .EnsureSuccessStatusCode();
        var first = await CreateHousehold(authenticated, "第一個家");
        var second = await CreateHousehold(authenticated, "第二個家");
        (await authenticated.PostAsJsonAsync(first + "/inventory", new { name = "雞蛋", expiresAt })).EnsureSuccessStatusCode();
        (await authenticated.PostAsJsonAsync(second + "/inventory", new { name = "蘋果", expiresAt })).EnsureSuccessStatusCode();

        var now = new DateTimeOffset(date.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(8));
        await phaseTwo.Services.GetRequiredService<LineService>().PushDue(now);
        Assert.DoesNotContain(recorder.Calls, call => call.Endpoint == "push");
        await SendWebhook(browser, "到期");
        Assert.Contains("重新綁定", recorder.Calls[^1].Text);
        Assert.DoesNotContain("舊家庭牛奶", recorder.Calls[^1].Text);

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync(first + "/line-binding", null)).StatusCode);
        var command = await BindingCommand(authenticated, first);
        await SendWebhook(browser, command);
        Assert.Contains("綁定完成", recorder.Calls[^1].Text);
        await SendWebhook(browser, "到期");
        Assert.Contains("雞蛋", recorder.Calls[^1].Text);
        Assert.DoesNotContain("蘋果", recorder.Calls[^1].Text);

        await SendWebhook(browser, await BindingCommand(authenticated, second));
        await SendWebhook(browser, command, "U2");
        Assert.Contains("失效", recorder.Calls[^1].Text);
        await SendWebhook(browser, "到期", "U2");
        Assert.Contains("重新綁定", recorder.Calls[^1].Text);
        await SendWebhook(browser, "到期");
        Assert.Contains("蘋果", recorder.Calls[^1].Text);
        Assert.DoesNotContain("雞蛋", recorder.Calls[^1].Text);
    }

    [Fact]
    public async Task PhaseTwoRetriesTheSameDailyRequestAfterRestartAndStopsAfterAcceptance()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        using var recorder = new Recorder();
        await using var phaseOne = database.CreateFactory(recorder);
        using var google = new GoogleTestIdentityProvider();
        await using var firstHost = google.Configure(phaseOne);
        using var owner = await SignIn(firstHost, google, "owner");
        var household = await CreateHousehold(owner, "Home");
        var other = await CreateHousehold(owner, "Other Home");
        var now = LineService.Taipei(DateTimeOffset.UtcNow);
        var day = DateOnly.FromDateTime(now.DateTime);
        now = new DateTimeOffset(day.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(8));
        var expiresAt = day.AddDays(1).ToString("yyyy-MM-dd");
        (await owner.PostAsJsonAsync(household + "/inventory", new { name = "Chicken", expiresAt })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync(other + "/inventory", new { name = "Other food", expiresAt })).EnsureSuccessStatusCode();
        await SendWebhook(owner, await BindingCommand(owner, household));
        await SendWebhook(owner, await BindingCommand(owner, other), "U2");

        var firstWorker = firstHost.Services.GetRequiredService<LineService>();
        await firstWorker.PushDue(now.AddHours(-2));
        Assert.DoesNotContain(recorder.Calls, call => call.Endpoint == "push");
        recorder.PushResults.Enqueue(HttpStatusCode.ServiceUnavailable);
        recorder.PushResults.Enqueue(HttpStatusCode.OK);
        await firstWorker.PushDue(now);
        var initial = recorder.Calls.Where(call => call.Endpoint == "push").ToArray();
        Assert.Equal(2, initial.Length);
        Assert.Contains("Chicken", Assert.Single(initial, call => call.To == "U1").Text);
        Assert.Contains("Other food", Assert.Single(initial, call => call.To == "U2").Text);

        (await owner.PostAsJsonAsync(household + "/inventory", new { name = "Later", expiresAt })).EnsureSuccessStatusCode();
        await using var restarted = google.Configure(phaseOne);
        using var browser = restarted.CreateClient();
        recorder.PushResults.Enqueue(HttpStatusCode.Conflict);
        recorder.PushResults.Enqueue(HttpStatusCode.Conflict);
        recorder.PushResults.Enqueue(HttpStatusCode.OK);
        var workers = restarted.Services.GetRequiredService<LineService>();
        await workers.PushDue(now); // A 409 lacking the accepted-request header is still a failure.
        recorder.ConflictAccepted = true;
        await workers.PushDue(now);
        await workers.PushDue(now);
        await SendWebhook(owner, await BindingCommand(owner, other));
        await workers.PushDue(now);
        var attempts = recorder.Calls.Where(call => call.Endpoint == "push" && call.To == "U1").ToArray();
        Assert.Equal(3, attempts.Length);
        Assert.True(Guid.TryParse(attempts[0].RetryKey, out _));
        Assert.All(attempts, attempt => Assert.Equal(attempts[0], attempt));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingOrLeavingRevokesOutstandingChallengesDueQueriesAndReminders(bool leave)
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        using var recorder = new Recorder();
        await using var phaseOne = database.CreateFactory(recorder);
        using var google = new GoogleTestIdentityProvider();
        await using var phaseTwo = google.Configure(phaseOne);
        using var owner = await SignIn(phaseTwo, google, "owner");
        using var member = await SignIn(phaseTwo, google, "member");
        var household = await CreateHousehold(owner, "Owner's home");
        var alternate = await CreateHousehold(member, "Member's home");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync(household + "/line-binding", null)).StatusCode);
        await Admit(owner, member, household);
        var consumed = await BindingCommand(member, household);
        var unconsumed = await BindingCommand(member, household);
        await SendWebhook(member, consumed);
        Assert.Contains("綁定完成", recorder.Calls[^1].Text);
        var now = TodayAtTen();
        var expiresAt = DateOnly.FromDateTime(now.DateTime).AddDays(2).ToString("yyyy-MM-dd");
        (await owner.PostAsJsonAsync(household + "/inventory", new { name = "雞蛋", expiresAt })).EnsureSuccessStatusCode();
        (await member.PostAsJsonAsync(alternate + "/inventory", new { name = "牛奶", expiresAt })).EnsureSuccessStatusCode();
        await SendWebhook(member, await BindingCommand(member, alternate), "U2");

        var memberId = (await member.GetFromJsonAsync<JsonElement>("/api/auth/session")).GetProperty("userId").GetGuid();
        using var response = await (leave ? member.DeleteAsync(household + "/members/me") : owner.DeleteAsync(household + "/members/" + memberId));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await SendWebhook(member, unconsumed);
        Assert.Contains("失效", recorder.Calls[^1].Text);
        await SendWebhook(member, "到期");
        Assert.Contains("重新綁定", recorder.Calls[^1].Text);
        await SendWebhook(member, "到期", "U2");
        Assert.Contains("牛奶", recorder.Calls[^1].Text);
        recorder.PushResults.Enqueue(HttpStatusCode.OK);
        await phaseTwo.Services.GetRequiredService<LineService>().PushDue(now);
        var sent = Assert.Single(recorder.Calls, call => call.Endpoint == "push");
        Assert.Equal("U2", sent.To);
        Assert.Contains("牛奶", sent.Text);

        await Admit(owner, member, household);
        await SendWebhook(member, "到期");
        Assert.Contains("重新綁定", recorder.Calls[^1].Text);
        await SendWebhook(member, unconsumed);
        Assert.Contains("失效", recorder.Calls[^1].Text);
    }

    [Fact]
    public async Task OnlyPrivateMessagesCanBindAndExpiredChallengesCannotBeUsed()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        using var recorder = new Recorder();
        await using var phaseOne = database.CreateFactory(recorder);
        using var google = new GoogleTestIdentityProvider();
        var clock = new ManualTime(DateTimeOffset.UtcNow);
        await using var phaseTwo = google.Configure(phaseOne, clock);
        using var owner = await SignIn(phaseTwo, google, "owner");
        var household = await CreateHousehold(owner, "Home");
        var expired = await BindingCommand(owner, household);
        clock.Now += TimeSpan.FromMinutes(10);
        await SendWebhook(owner, expired);
        Assert.Contains("失效", recorder.Calls[^1].Text);
        await SendWebhook(owner, "綁定 fake");
        Assert.Contains("失效", recorder.Calls[^1].Text);
        await SendWebhook(owner, "", type: "follow");
        Assert.Contains("重新綁定", recorder.Calls[^1].Text);
        var command = await BindingCommand(owner, household);
        var count = recorder.Calls.Count;
        await SendWebhook(owner, command, sourceType: "group");
        await SendWebhook(owner, "到期", sourceType: "room");
        await SendWebhook(owner, command, userId: null);
        await SendWebhook(owner, command, replyToken: null);
        await SendWebhook(owner, command, messageType: "image");
        await SendWebhook(owner, command, type: "postback");
        Assert.Equal(count, recorder.Calls.Count);
        await SendWebhook(owner, command);
        Assert.Contains("綁定完成", recorder.Calls[^1].Text);
        await SendWebhook(owner, "到期");
        Assert.Contains("目前沒有三天內到期的食材", recorder.Calls[^1].Text);

        await phaseTwo.Services.GetRequiredService<LineService>().PushDue(TodayAtTen());
        Assert.DoesNotContain(recorder.Calls, call => call.Endpoint == "push");

        recorder.ReplyStatus = HttpStatusCode.ServiceUnavailable;
        await SendWebhook(owner, "ping");
        Assert.Contains("pong", recorder.Calls[^1].Text);
        recorder.ReplyStatus = HttpStatusCode.OK;
        await SendWebhook(owner, "取消訂閱");
        Assert.Contains("取消訂閱", recorder.Calls[^1].Text);
        await SendWebhook(owner, "到期");
        Assert.Contains("重新綁定", recorder.Calls[^1].Text);
        await SendWebhook(owner, await BindingCommand(owner, household));
        await SendWebhook(owner, "", type: "unfollow", replyToken: null);
        await SendWebhook(owner, "到期");
        Assert.Contains("重新綁定", recorder.Calls[^1].Text);
    }

    [Fact]
    public async Task WorkerSkipsQueuedCandidatesWhoseMembershipOrBindingChangedWhileItWasSending()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        using var recorder = new Recorder();
        await using var phaseOne = database.CreateFactory(recorder);
        using var google = new GoogleTestIdentityProvider();
        await using var phaseTwo = google.Configure(phaseOne);
        using var owner = await SignIn(phaseTwo, google, "owner");
        using var member = await SignIn(phaseTwo, google, "member");
        var blockerHome = await CreateHousehold(owner, "Blocker");
        var departingHome = await CreateHousehold(owner, "Departing");
        var oldHome = await CreateHousehold(owner, "Old");
        var newHome = await CreateHousehold(owner, "New");
        await Admit(owner, member, departingHome);
        var now = TodayAtTen();
        var expiresAt = DateOnly.FromDateTime(now.DateTime).AddDays(2).ToString("yyyy-MM-dd");
        foreach (var address in new[] { blockerHome, departingHome, oldHome, newHome })
            (await owner.PostAsJsonAsync(address + "/inventory", new { name = address, expiresAt })).EnsureSuccessStatusCode();

        await SendWebhook(owner, await BindingCommand(owner, blockerHome), "U0");
        await SendWebhook(member, await BindingCommand(member, departingHome), "U1");
        await SendWebhook(owner, await BindingCommand(owner, oldHome), "U2");
        var newer = await BindingCommand(owner, newHome);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.WaitForPush = (_, ct) =>
        {
            entered.TrySetResult();
            return release.Task.WaitAsync(ct);
        };
        recorder.PushResults.Enqueue(HttpStatusCode.OK);
        var worker = phaseTwo.Services.GetRequiredService<LineService>();
        var sending = worker.PushDue(now);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync(departingHome + "/members/me")).StatusCode);
            await SendWebhook(owner, newer, "U2");
        }
        finally
        {
            release.TrySetResult();
        }

        await sending;
        Assert.Equal("U0", Assert.Single(recorder.Calls, call => call.Endpoint == "push").To);
        recorder.PushResults.Enqueue(HttpStatusCode.OK);
        await worker.PushDue(now);
        var second = Assert.Single(recorder.Calls, call => call.Endpoint == "push" && call.To == "U2");
        Assert.Contains(newHome, second.Text);
        Assert.DoesNotContain(oldHome, second.Text);
    }

    [Fact]
    public async Task EnabledWorkerPicksUpAHouseholdReminderAndReleasesLocksOnShutdown()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        using var recorder = new Recorder();
        await using var phaseOne = database.CreateFactory(recorder);
        using var google = new GoogleTestIdentityProvider();
        var reminderTime = TodayAtTen().ToUniversalTime();
        var clock = new ManualTime(reminderTime.AddHours(-2)) { TimerPeriod = TimeSpan.FromMilliseconds(25) };
        await using var phaseTwo = google.Configure(phaseOne, clock);
        using var owner = await SignIn(phaseTwo, google, "owner");
        using var member = await SignIn(phaseTwo, google, "member");
        var household = await CreateHousehold(owner, "Home");
        await Admit(owner, member, household);
        var expiresAt = DateOnly.FromDateTime(clock.Now.DateTime).AddDays(1).ToString("yyyy-MM-dd");
        (await owner.PostAsJsonAsync(household + "/inventory", new { name = "豆腐", expiresAt })).EnsureSuccessStatusCode();
        await SendWebhook(member, await BindingCommand(member, household));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.WaitForPush = (_, ct) =>
        {
            entered.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        var firstCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnRead = () => firstCheck.TrySetResult();
        await using var running = phaseTwo.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DisableWorker"] = "false",
            })));
        using var browser = running.CreateClient();
        await firstCheck.Task.WaitAsync(TimeSpan.FromSeconds(4));
        clock.Now = reminderTime;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Contains("豆腐", Assert.Single(recorder.Calls, call => call.Endpoint == "push").Text);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await running.Services.GetServices<IHostedService>().OfType<ExpiryWorker>().Single().StopAsync(timeout.Token);
        using var departure = await member.DeleteAsync(household + "/members/me", timeout.Token);
        Assert.Equal(HttpStatusCode.NoContent, departure.StatusCode);
        await phaseTwo.Services.GetRequiredService<LineService>().PushDue(clock.Now);
        Assert.Single(recorder.Calls, call => call.Endpoint == "push");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeletionRevokesLineBindingAndOutstandingChallenges(bool account)
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        using var recorder = new Recorder();
        await using var phaseOne = database.CreateFactory(recorder);
        using var google = new GoogleTestIdentityProvider();
        await using var phaseTwo = google.Configure(phaseOne);
        using var owner = await SignIn(phaseTwo, google, "owner");
        using var member = await SignIn(phaseTwo, google, "member");
        using var browser = phaseTwo.CreateClient();
        var household = await CreateHousehold(owner, "Home");
        await Admit(owner, member, household);
        var now = TodayAtTen();
        var expiresAt = DateOnly.FromDateTime(now.DateTime).AddDays(1).ToString("yyyy-MM-dd");
        (await owner.PostAsJsonAsync(household + "/inventory", new { name = "豆腐", expiresAt })).EnsureSuccessStatusCode();
        await SendWebhook(browser, await BindingCommand(member, household));
        var outstanding = await BindingCommand(member, household);
        await SendWebhook(browser, "到期");
        Assert.Contains("豆腐", recorder.Calls[^1].Text);

        var path = account ? "/api/auth/account" : household;
        using var deleted = await (account ? member : owner).DeleteAsync(path + "?confirm=true");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await SendWebhook(browser, outstanding, "U2");
        Assert.Contains("失效", recorder.Calls[^1].Text);
        await SendWebhook(browser, "到期");
        Assert.Contains("重新綁定", recorder.Calls[^1].Text);
        Assert.DoesNotContain("豆腐", recorder.Calls[^1].Text);
        await phaseTwo.Services.GetRequiredService<LineService>().PushDue(now);
        Assert.DoesNotContain(recorder.Calls, call => call.Endpoint == "push");
    }

    private static async Task Admit(HttpClient owner, HttpClient member, string household)
    {
        using var invite = await owner.PostAsJsonAsync(household + "/invitations", new { email = "member@gmail.com" });
        invite.EnsureSuccessStatusCode();
        var token = (await invite.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        (await member.PostAsJsonAsync("/api/households/invitations/redeem", new { token })).EnsureSuccessStatusCode();
    }

    private static DateTimeOffset TodayAtTen()
    {
        var date = DateOnly.FromDateTime(LineService.Taipei(DateTimeOffset.UtcNow).DateTime);
        return new DateTimeOffset(date.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(8));
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private long utcTicks = now.UtcTicks;
        public Action? OnRead { get; set; }
        public TimeSpan? TimerPeriod { get; init; }
        public DateTimeOffset Now
        {
            get => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref utcTicks, value.UtcTicks);
        }

        public override DateTimeOffset GetUtcNow()
        {
            var time = Now;
            OnRead?.Invoke();
            return time;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, TimerPeriod ?? dueTime, TimerPeriod ?? period);
    }

    private static async Task<HttpClient> SignIn(WebApplicationFactory<Program> host, GoogleTestIdentityProvider google, string subject)
    {
        var browser = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        using var response = await browser.PostAsJsonAsync("/api/auth/google",
            new { credential = google.Credential(subject, subject + "@gmail.com") });
        response.EnsureSuccessStatusCode();
        return browser;
    }

    private static async Task<string> CreateHousehold(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/households", new { name });
        response.EnsureSuccessStatusCode();
        return "/api/households/" + (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<string> BindingCommand(HttpClient client, string household)
    {
        using var response = await client.PostAsync(household + "/line-binding", null);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("command").GetString()!;
    }
}
