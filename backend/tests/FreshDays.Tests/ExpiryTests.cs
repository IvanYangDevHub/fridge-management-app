using FreshDays;
using Xunit;

namespace FreshDays.Tests;

public class ExpiryTests
{
    [Fact]
    public void IncludesExpiredTodayAndNextThreeDaysButExcludesConsumedAndLater()
    {
        var today = new DateOnly(2026, 11, 7);
        var items = new[] {
            new FoodItem("one", "牛奶", "2026-11-10", "active"),
            new FoodItem("two", "蛋", "2026-11-06", "active"),
            new FoodItem("three", "青菜", "2026-11-07", "active"),
            new FoodItem("four", "肉", "2026-11-11", "active"),
            new FoodItem("five", "麵", "2026-11-07", "consumed"),
            new FoodItem("six", "鹽", null, "active")
        };
        Assert.Equal(new[] { "蛋", "青菜", "牛奶" }, Expiry.Due(items, today).Select(x => x.Name));
        Assert.Contains("已過期", Expiry.Message(items, today));
        Assert.Contains("今天到期", Expiry.Message(items, today));
    }

    [Fact]
    public void ReturnsNoMessageWhenNoFoodIsDue() => Assert.Null(Expiry.Message([], new DateOnly(2026, 11, 7)));
}
