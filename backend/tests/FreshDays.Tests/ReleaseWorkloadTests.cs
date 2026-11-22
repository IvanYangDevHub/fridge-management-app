using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Xunit.Abstractions;

namespace FreshDays.Tests;

[CollectionDefinition("Release workload", DisableParallelization = true)]
public class ReleaseWorkloadCollection;

[Collection("Release workload")]
public class ReleaseWorkloadTests(ITestOutputHelper output)
{
    private const int HouseholdCount = 100;
    private const int MembersPerHousehold = 5;
    private const int HouseholdsPerWave = 10;
    private static readonly string[] Lists = ["inventory", "shopping"];

    [Fact]
    public async Task FiftyConcurrentMembersKeepBothListsConsistentAcrossOneHundredHouseholds()
    {
        await using var database = new PostgresTestDatabase();
        await database.Initialize();
        await using var phaseOne = database.CreateFactory(pooling: true);
        using var google = new GoogleTestIdentityProvider();
        await using var factory = google.Configure(phaseOne);
        var households = new List<Household>();
        var seedTimer = Stopwatch.StartNew();
        for (var start = 0; start < HouseholdCount; start += HouseholdsPerWave)
        {
            foreach (var household in await Task.WhenAll(Enumerable.Range(start, HouseholdsPerWave).Select(SeedHousehold)))
                households.Add(household);
        }
        output.WriteLine($"Seeded {households.Count} households with {households.Sum(h => h.Members.Length)} authenticated users in {seedTimer.Elapsed.TotalSeconds:F2}s.");

        var observed = households[0];
        var observer = observed.Members[^1].Browser;
        await AssertBothLists(observer, observed, "Initial ", 1);
        var pollClock = Stopwatch.StartNew();
        var nextPoll = Task.Delay(TimeSpan.FromSeconds(15));
        var writesTimer = Stopwatch.StartNew();
        foreach (var wave in households.Chunk(HouseholdsPerWave))
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var users = wave.SelectMany(household => household.Members.Select(member => UpdateBothLists(household, member))).ToArray();
            Assert.Equal(50, users.Length);
            release.SetResult();
            await Task.WhenAll(users);

            async Task UpdateBothLists(Household household, Member member)
            {
                await release.Task;
                foreach (var list in Lists)
                {
                    using var response = await member.Browser.PutAsJsonAsync(household.Item(list, member.Items[list]),
                        Change(list, member.Name, 1));
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64());

                    var other = households[(household.Number + 1) % HouseholdCount];
                    using var blocked = await member.Browser.PutAsJsonAsync(other.Item(list, other.Members[0].Items[list]),
                        Change(list, "Forbidden edit", 1));
                    Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
                    using var outside = await member.Browser.GetAsync(other.Path(list));
                    Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);
                }
            }
        }
        output.WriteLine($"Committed {HouseholdCount * MembersPerHousehold * Lists.Length} distinct edits and checked isolation in {writesTimer.Elapsed.TotalSeconds:F2}s.");

        await nextPoll;
        await AssertBothLists(observer, observed, "", 2);
        output.WriteLine($"An open session saw all five members' edits on both lists at {pollClock.Elapsed.TotalSeconds:F2}s.");
        Assert.True(pollClock.Elapsed < TimeSpan.FromSeconds(17), "The 15-second poll did not complete within 17 seconds.");

        var readTimer = Stopwatch.StartNew();
        foreach (var wave in households.Chunk(HouseholdsPerWave))
            await Task.WhenAll(wave.SelectMany(household => household.Members.Select(member => AssertBothLists(member.Browser, household, "", 2))));
        output.WriteLine($"All {HouseholdCount * MembersPerHousehold} users verified both committed lists in {readTimer.Elapsed.TotalSeconds:F2}s.");

        var shared = households[0];
        foreach (var list in Lists)
        {
            var id = shared.Members[0].Items[list];
            var attempts = await Task.WhenAll(shared.Members.Select(async member =>
            {
                using var response = await member.Browser.PutAsJsonAsync(shared.Item(list, id), Change(list, member.Name + " racer", 2));
                return (response.StatusCode, member.Name);
            }));
            var winner = Assert.Single(attempts, attempt => attempt.StatusCode == HttpStatusCode.OK);
            Assert.Equal(MembersPerHousehold - 1, attempts.Count(attempt => attempt.StatusCode == HttpStatusCode.Conflict));
            var items = await shared.Members[1].Browser.GetFromJsonAsync<JsonElement>(shared.Path(list));
            var record = Assert.Single(items.EnumerateArray(), item => Id(item) == id);
            Assert.Equal(winner.Name + " racer", record.GetProperty("name").GetString());
            Assert.Equal(3, record.GetProperty("version").GetInt64());
        }

        async Task<Household> SeedHousehold(int number)
        {
            var members = new Member[MembersPerHousehold];
            for (var index = 0; index < members.Length; index++)
            {
                var name = $"member-{number:D3}-{index}";
                var email = $"{name}@gmail.com";
                var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
                await Post(browser, "/api/auth/google", new { credential = google.Credential(name, email) }, HttpStatusCode.OK);
                members[index] = new Member(name, email, browser, new Dictionary<string, string>());
            }
            var owner = members[0].Browser;
            var created = await Post(owner, "/api/households", new { name = $"Household {number:D3}" }, HttpStatusCode.Created);
            var household = new Household(number, Id(created), members);
            foreach (var member in members.Skip(1))
            {
                var invitation = await Post(owner, household.Path("invitations"), new { member.Email }, HttpStatusCode.Created);
                await Post(member.Browser, "/api/households/invitations/redeem",
                    new { token = invitation.GetProperty("token").GetString() }, HttpStatusCode.Created);
            }
            foreach (var member in members)
            {
                foreach (var list in Lists)
                {
                    var name = "Initial " + member.Name;
                    object body = list == "shopping" ? new { name, completed = false } : new { name };
                    var initial = await Post(member.Browser, household.Path(list), body, HttpStatusCode.Created);
                    member.Items.Add(list, Id(initial));
                }
            }
            return household;
        }
    }

    private static object Change(string list, string name, int version) =>
        list == "shopping" ? new { name, version, completed = true } : new { name, version };

    private static async Task AssertBothLists(HttpClient browser, Household household, string namePrefix, long version)
    {
        foreach (var list in Lists)
        {
            var items = await browser.GetFromJsonAsync<JsonElement>(household.Path(list));
            var expected = household.Members.Select(member => (Id: member.Items[list], Name: namePrefix + member.Name, Version: version));
            var actual = items.EnumerateArray().Select(item => (Id: Id(item), Name: item.GetProperty("name").GetString()!,
                Version: item.GetProperty("version").GetInt64()));
            Assert.Equal(expected.OrderBy(item => item.Name), actual.OrderBy(item => item.Name));
            if (list == "shopping")
                Assert.All(items.EnumerateArray(), item => Assert.Equal(version != 1, item.GetProperty("completed").GetBoolean()));
        }
    }

    private static async Task<JsonElement> Post(HttpClient browser, string path, object body, HttpStatusCode expected)
    {
        using var response = await browser.PostAsJsonAsync(path, body);
        Assert.True(response.StatusCode == expected, $"POST {path}: expected {expected}, received {response.StatusCode}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string Id(JsonElement item) => item.GetProperty("id").GetString()!;

    private sealed record Member(string Name, string Email, HttpClient Browser, Dictionary<string, string> Items);

    private sealed record Household(int Number, string Id, Member[] Members)
    {
        public string Path(string list) => $"/api/households/{Id}/{list}";
        public string Item(string list, string id) => $"{Path(list)}/item?id={Uri.EscapeDataString(id)}";
    }
}
