using FreshDays;
using Xunit;

namespace FreshDays.Tests;

public class InventoryTests
{
    [Fact]
    public async Task SqlitePersistsInventoryAndSubscriptionsAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"freshdays-{Guid.NewGuid():N}.db");
        try
        {
            var first = new SqliteStore(path);
            await first.Initialize();
            await first.ReplaceInventory("[{\"id\":\"1\",\"name\":\"牛奶\",\"expiresAt\":\"2026-11-08\",\"status\":\"active\"}]");
            await first.Subscribe("U1");
            await first.Subscribe("U1");
            var second = new SqliteStore(path);
            await second.Initialize();
            Assert.Contains("牛奶", await second.ReadInventory());
            Assert.Equal(new[] { "U1" }, await second.Subscribers());
            await second.Unsubscribe("U1");
            Assert.Empty(await second.Subscribers());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
