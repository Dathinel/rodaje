using Rodaje.Playback.Composition;
using Xunit;

namespace Rodaje.Playback.Tests;

public class RenderCacheKeyTests
{
    [Fact]
    public void Ida_y_vuelta_por_texto_y_bytes()
    {
        byte[] hash = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];
        RenderCacheKey key = RenderCacheKey.FromHash(hash);

        Assert.Equal("0102030405060708090a0b0c0d0e0f10", key.ToString());
        Assert.True(RenderCacheKey.TryParse(key.ToString(), out RenderCacheKey parsed));
        Assert.Equal(key, parsed);
        Span<byte> bytes = stackalloc byte[RenderCacheKey.Size];
        key.WriteTo(bytes);
        Assert.Equal(hash.AsSpan(0, 16).ToArray(), bytes.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0102030405060708090a0b0c0d0e0f1")]
    [InlineData("0102030405060708090a0b0c0d0e0fzz")]
    public void Rechaza_texto_que_no_es_una_huella(string? text) => Assert.False(RenderCacheKey.TryParse(text, out _));

    [Fact]
    public void Exige_16_bytes() => Assert.Throws<ArgumentException>(() => RenderCacheKey.FromHash(new byte[15]));
}
