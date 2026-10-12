namespace Rodaje.TestServer.Tests;

public sealed class ByteRangeTests
{
    [Theory]
    [InlineData("bytes=0-9", 100, 0, 9)]
    [InlineData("bytes=10-", 100, 10, 99)]
    [InlineData("bytes=-10", 100, 90, 99)]
    [InlineData("bytes=-500", 100, 0, 99)]
    [InlineData("bytes=50-500", 100, 50, 99)]
    [InlineData(" BYTES=1 - 2 ", 100, 1, 2)]
    public void Parses_satisfiable_ranges(string header, long length, long first, long last)
    {
        var result = ByteRange.TryParse(header, length, out var range);

        Assert.Equal(ByteRange.ParseResult.Satisfiable, result);
        Assert.Equal(new ByteRange(first, last), range);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("items=0-1")]
    [InlineData("bytes=0-1,5-6")]
    [InlineData("bytes=abc")]
    [InlineData("bytes=5-2")]
    [InlineData("bytes=x-")]
    [InlineData("bytes=-x")]
    [InlineData("bytes=+1-2")]
    public void Ignores_what_it_does_not_understand(string? header)
    {
        Assert.Equal(ByteRange.ParseResult.Ignore, ByteRange.TryParse(header, 100, out _));
    }

    [Theory]
    [InlineData("bytes=100-", 100)]
    [InlineData("bytes=200-300", 100)]
    [InlineData("bytes=-0", 100)]
    [InlineData("bytes=-5", 0)]
    [InlineData("bytes=0-", 0)]
    public void Reports_unsatisfiable_ranges(string header, long length)
    {
        Assert.Equal(ByteRange.ParseResult.Unsatisfiable, ByteRange.TryParse(header, length, out _));
    }

    [Fact]
    public void Length_is_inclusive()
    {
        Assert.Equal(10, new ByteRange(0, 9).Length);
    }
}
