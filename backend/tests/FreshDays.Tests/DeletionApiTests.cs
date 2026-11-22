using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace FreshDays.Tests;

public sealed partial class MembershipApiTests
{
    [Fact]
    public async Task AccountDeletionRevokesSessionsAndAnonymizesSharedItemsAcrossCurrentAndFormerHouseholds()
    {
        await using var app = await TestApp.Start();
        using var owner = await app.SignIn("owner");
        using var member = await app.SignIn("member");
        using var otherSession = await app.SignIn("member");
        using var anonymous = app.Browser();
        var memberId = await UserId(member);
        var household = await CreateHousehold(owner, "Home");
        using var legacy = app.LegacyBrowser();
        using var legacyItems = await legacy.PutAsJsonAsync("/api/shopping", new[]
        {
            new { id = "old-tea", name = "Tea", completed = true, createdBy = "member@gmail.com", brand = "Local" },
        });
        legacyItems.EnsureSuccessStatusCode();
        using var claimed = await owner.PostAsync("/api/households/legacy/claim", null);
        claimed.EnsureSuccessStatusCode();
        var former = claimed.Headers.Location!.OriginalString;
        await Admit(owner, member, household, "member");
        await Admit(owner, member, former, "member");

        (await member.PostAsJsonAsync(household + "/inventory", new { name = "Apples", createdBy = memberId, quantity = 2 }))
            .EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync(household + "/inventory", new { name = "Kiwi", createdBy = await UserId(owner) }))
            .EnsureSuccessStatusCode();
        (await member.PostAsJsonAsync(former + "/inventory", new { name = "Mango", createdBy = "Member@gmail.com" }))
            .EnsureSuccessStatusCode();
        (await member.PostAsJsonAsync(household + "/shopping", new { name = "Rice", completed = false })).EnsureSuccessStatusCode();
        (await member.DeleteAsync(former + "/members/me")).EnsureSuccessStatusCode();

        const string path = "/api/auth/account";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync(path + "?confirm=true")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await member.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await member.DeleteAsync(path + "?confirm=false")).StatusCode);

        using var deletion = await member.DeleteAsync(path + "?confirm=true");
        Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
        Assert.Equal("no-store", deletion.Headers.CacheControl?.ToString());
        Assert.Contains(deletion.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("__Host-freshdays-session=;") && value.Contains("expires="));
        foreach (var session in new[] { member, otherSession })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/auth/session")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync(household + "/shopping")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await session.DeleteAsync(path + "?confirm=true")).StatusCode);
        }

        var items = (await owner.GetFromJsonAsync<JsonElement>(household + "/inventory")).EnumerateArray().ToArray();
        var apples = Assert.Single(items, item => item.GetProperty("name").GetString() == "Apples");
        Assert.Equal(2, apples.GetProperty("quantity").GetInt32());
        Assert.False(apples.TryGetProperty("createdBy", out _));
        Assert.Equal(2, apples.GetProperty("version").GetInt64());
        var kiwi = Assert.Single(items, item => item.GetProperty("name").GetString() == "Kiwi");
        Assert.Equal(await UserId(owner), kiwi.GetProperty("createdBy").GetGuid());
        var mango = Assert.Single((await owner.GetFromJsonAsync<JsonElement>(former + "/inventory")).EnumerateArray());
        Assert.Equal("Mango", mango.GetProperty("name").GetString());
        Assert.False(mango.TryGetProperty("createdBy", out _));
        var tea = Assert.Single((await owner.GetFromJsonAsync<JsonElement>(former + "/shopping")).EnumerateArray());
        Assert.Equal("old-tea", tea.GetProperty("id").GetString());
        Assert.Equal("Local", tea.GetProperty("brand").GetString());
        Assert.True(tea.GetProperty("completed").GetBoolean());
        Assert.False(tea.TryGetProperty("createdBy", out _));
        var shopping = Assert.Single((await owner.GetFromJsonAsync<JsonElement>(household + "/shopping")).EnumerateArray());
        Assert.Equal("Rice", shopping.GetProperty("name").GetString());
        Assert.Single((await owner.GetFromJsonAsync<JsonElement>(household + "/members")).EnumerateArray());

        using var signedInAgain = await app.SignIn("member");
        Assert.NotEqual(memberId, await UserId(signedInAgain));
        Assert.Empty((await signedInAgain.GetFromJsonAsync<JsonElement>("/api/households")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await signedInAgain.DeleteAsync(path + "?confirm=true")).StatusCode);
    }

    [Fact]
    public async Task AccountDeletionIncludesHouseholdsJoinedWhileWaitingOnAnExistingMembership()
    {
        await using var app = await TestApp.Start();
        using var owner = await app.SignIn("owner");
        using var member = await app.SignIn("member");
        var household = await CreateHousehold(owner, "First");
        var other = await CreateHousehold(owner, "Second");
        await Admit(owner, member, household, "member");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var blocker = new NpgsqlConnection(app.ConnectionString);
        await blocker.OpenAsync(timeout.Token);
        await using var transaction = await blocker.BeginTransactionAsync(timeout.Token);
        await using var hold = new NpgsqlCommand("SELECT id FROM households WHERE id = @id FOR UPDATE", blocker, transaction);
        hold.Parameters.AddWithValue("id", Guid.Parse(household.Split('/')[^1]));
        await hold.ExecuteScalarAsync(timeout.Token);

        var deletion = member.DeleteAsync("/api/auth/account?confirm=true", timeout.Token);
        await using var observer = new NpgsqlConnection(app.ConnectionString);
        await observer.OpenAsync(timeout.Token);
        await using var blocked = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @blocker = ANY (pg_blocking_pids(pid)));
            """, observer);
        blocked.Parameters.AddWithValue("blocker", blocker.ProcessID);
        while (await blocked.ExecuteScalarAsync(timeout.Token) is not true)
            await Task.Delay(25, timeout.Token);

        await Admit(owner, member, other, "member");
        await transaction.CommitAsync(timeout.Token);
        using var response = await deletion;
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Single((await owner.GetFromJsonAsync<JsonElement>(household + "/members")).EnumerateArray());
        Assert.Single((await owner.GetFromJsonAsync<JsonElement>(other + "/members")).EnumerateArray());
    }

    [Fact]
    public async Task AccountDeletionRequiresTransferringOrDeletingEveryOwnedHousehold()
    {
        await using var app = await TestApp.Start();
        using var owner = await app.SignIn("owner");
        using var successor = await app.SignIn("successor");
        var shared = await CreateHousehold(owner, "Shared");
        var solo = await CreateHousehold(owner, "Solo");
        await Admit(owner, successor, shared, "successor");
        const string deletion = "/api/auth/account?confirm=true";

        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync(deletion)).StatusCode);
        var successorId = await UserId(successor);
        (await owner.PostAsync(shared + "/members/" + successorId + "/transfer", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync(deletion)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(shared + "/inventory")).StatusCode);

        (await owner.DeleteAsync(solo + "?confirm=true")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(deletion)).StatusCode);
        Assert.Equal(successorId, await CurrentOwner(successor, shared));
    }

    [Fact]
    public async Task HouseholdDeletionRequiresConfirmedOwnershipAndRevokesEveryMembersAccess()
    {
        await using var app = await TestApp.Start();
        using var anonymous = app.Browser();
        using var owner = await app.SignIn("owner");
        using var member = await app.SignIn("member");
        using var outsider = await app.SignIn("outsider");
        var household = await CreateHousehold(owner, "Home");
        var other = await CreateHousehold(owner, "Other home");
        await Admit(owner, member, household, "member");
        using var food = await member.PostAsJsonAsync(household + "/inventory", new { name = "Apples" });
        Assert.Equal(HttpStatusCode.Created, food.StatusCode);
        using var invitation = await owner.PostAsJsonAsync(household + "/invitations", new { email = "outsider@gmail.com" });
        var invitationToken = (await invitation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var confirmed = household + "?confirm=true";

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync(confirmed)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync(confirmed)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.DeleteAsync(confirmed)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.DeleteAsync(household)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.DeleteAsync(household + "?confirm=false")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(household + "/inventory")).StatusCode);

        using var deletion = await owner.DeleteAsync(confirmed);
        Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
        Assert.Equal("no-store", deletion.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.DeleteAsync(confirmed)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(household + "/inventory")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(household + "/shopping")).StatusCode);
        Assert.False((await outsider.PostAsJsonAsync("/api/households/invitations/redeem", new { token = invitationToken })).IsSuccessStatusCode);
        Assert.Empty((await member.GetFromJsonAsync<JsonElement>("/api/households")).EnumerateArray());
        var surviving = Assert.Single((await owner.GetFromJsonAsync<JsonElement>("/api/households")).EnumerateArray());
        Assert.Equal(other, "/api/households/" + surviving.GetProperty("id").GetGuid());
    }
}
