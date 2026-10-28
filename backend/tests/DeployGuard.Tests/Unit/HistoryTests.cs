using DeployGuard.Api.History;

namespace DeployGuard.Tests.Unit;

public class HistoryTests
{
    [Fact]
    public void Cursor_round_trips()
    {
        var cursor = new HistoryCursor(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero), 42);

        Assert.True(HistoryCursor.TryDecode(cursor.Encode(), out var decoded));
        Assert.Equal(cursor, decoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("!!!")]
    [InlineData("bm90LWEtY3Vyc29y")] // "not-a-cursor"
    public void Invalid_cursor_is_rejected(string value)
    {
        Assert.False(HistoryCursor.TryDecode(value, out _));
    }

    [Fact]
    public void Percentile_uses_nearest_rank()
    {
        int[] sorted = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];

        Assert.Equal(100, HistoryEndpoints.Percentile(sorted, 0.95));
        Assert.Equal(50, HistoryEndpoints.Percentile(sorted, 0.50));
        Assert.Null(HistoryEndpoints.Percentile([], 0.95));
    }
}
