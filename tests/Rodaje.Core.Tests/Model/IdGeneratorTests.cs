using Rodaje.Core.Model;

namespace Rodaje.Core.Tests.Model;

public class IdGeneratorTests
{
    [Fact]
    public void Sequential_CountsAcrossPrefixesFromOne()
    {
        var ids = new SequentialIdGenerator();
        Assert.Equal("clip-1", ids.NewId("clip"));
        Assert.Equal("clip-2", ids.NewId("clip"));
        Assert.Equal("track-3", ids.NewId("track"));
        Assert.Equal("fx-4", ids.NewId("fx"));
    }

    [Fact]
    public void Sequential_IsDeterministicPerInstance()
    {
        var a = new SequentialIdGenerator();
        var b = new SequentialIdGenerator();
        string[] fromA = [.. Enumerable.Range(0, 5).Select(_ => a.NewId("c"))];
        string[] fromB = [.. Enumerable.Range(0, 5).Select(_ => b.NewId("c"))];
        Assert.Equal(fromA, fromB);
        Assert.Equal(["c-1", "c-2", "c-3", "c-4", "c-5"], fromA);
    }

    [Fact]
    public void Sequential_IsThreadSafe()
    {
        var ids = new SequentialIdGenerator();
        var bag = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 10_000, _ => bag.Add(ids.NewId("x")));
        Assert.Equal(10_000, bag.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("x-10000", bag);
    }

    [Fact]
    public void Sequential_UsesInvariantDigits()
    {
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            var ids = new SequentialIdGenerator();
            Assert.Equal("a-1", ids.NewId("a"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Guid_IdsArePrefixedAndUnique()
    {
        GuidIdGenerator ids = GuidIdGenerator.Instance;
        Assert.Same(GuidIdGenerator.Instance, ids);

        string[] generated = [.. Enumerable.Range(0, 1000).Select(_ => ids.NewId("clip"))];
        Assert.Equal(1000, generated.Distinct(StringComparer.Ordinal).Count());
        Assert.All(generated, id =>
        {
            Assert.StartsWith("clip-", id, StringComparison.Ordinal);
            string hex = id["clip-".Length..];
            Assert.Equal(32, hex.Length);
            Assert.True(Guid.TryParseExact(hex, "N", out _));
        });

        Assert.StartsWith("track-", new GuidIdGenerator().NewId("track"), StringComparison.Ordinal);
    }
}
