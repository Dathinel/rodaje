using Rodaje.App.Services.Media;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Timing;
using Rodaje.Media.Video;

namespace Rodaje.App.Tests.Media;

public sealed class FilmstripLayoutTests
{
    private static readonly Time Second = Time.FromSeconds(1);

    [Theory]
    [InlineData(1, 32)]
    [InlineData(32, 32)]
    [InlineData(33, 48)]
    [InlineData(72, 96)]
    [InlineData(200, 256)]
    [InlineData(4000, 256)]
    public void Heights_round_up_to_a_class(int requested, int expected) => Assert.Equal(expected, FilmstripLayout.HeightClass(requested));

    [Fact]
    public void Grid_step_is_the_largest_power_of_two_of_the_base_not_above_the_spacing()
    {
        Assert.Equal(FilmstripLayout.BaseStep, FilmstripLayout.GridStep(Time.FromTicks(1)));
        Assert.Equal(Time.FromSeconds(1), FilmstripLayout.GridStep(Time.FromSeconds(1.9)));
        Assert.Equal(Time.FromSeconds(2), FilmstripLayout.GridStep(Time.FromSeconds(2)));
        Assert.Equal(Time.FromSeconds(0.25), FilmstripLayout.GridStep(Time.FromSeconds(0.3)));
    }

    [Fact]
    public void Times_are_inside_the_range_ordered_and_on_the_grid()
    {
        Time start = Time.FromSeconds(3.3);
        Time end = Time.FromSeconds(13.7);

        Time[] times = FilmstripLayout.Times(start, end, 10);

        Assert.Equal(10, times.Length);
        Assert.All(times, t => Assert.InRange(t.Ticks, start.Ticks, end.Ticks - 1));
        Assert.Equal(times.Order(), times);
        Time step = FilmstripLayout.GridStep(Time.FromTicks((end - start).Ticks / 10));
        Assert.All(times[1..^1], t => Assert.Equal(0, t.Ticks % step.Ticks));
    }

    [Fact]
    public void Zooming_in_keeps_the_thumbnails_of_the_farther_zoom()
    {
        // Al duplicar la cantidad de huecos sobre el mismo tramo, la rejilla fina contiene a la gruesa.
        Time[] far = FilmstripLayout.Times(Time.Zero, Time.FromSeconds(64), 16);
        Time[] near = FilmstripLayout.Times(Time.Zero, Time.FromSeconds(64), 32);

        Assert.True(far.ToHashSet().IsSubsetOf(near));
    }

    [Fact]
    public void Scrolling_reuses_the_same_instants()
    {
        Time[] a = FilmstripLayout.Times(Time.FromSeconds(10), Time.FromSeconds(30), 20);
        Time[] b = FilmstripLayout.Times(Time.FromSeconds(12), Time.FromSeconds(32), 20);

        Assert.True(a.Intersect(b).Count() >= 16);
    }

    [Fact]
    public void Degenerate_requests_give_nothing()
    {
        Assert.Empty(FilmstripLayout.Times(Second, Second, 5));
        Assert.Empty(FilmstripLayout.Times(Second, Time.Zero, 5));
        Assert.Empty(FilmstripLayout.Times(Time.Zero, Second, 0));
        Assert.Equal(FilmstripLayout.MaxCount, FilmstripLayout.Times(Time.Zero, Time.FromSeconds(3600), 100_000).Length);
    }

    [Fact]
    public void Snap_rounds_to_the_nearest_grid_point_also_below_zero()
    {
        Time step = Time.FromSeconds(1);
        Assert.Equal(Time.FromSeconds(2), FilmstripLayout.Snap(Time.FromSeconds(1.5), step));
        Assert.Equal(Time.FromSeconds(1), FilmstripLayout.Snap(Time.FromSeconds(1.49), step));
        Assert.Equal(Time.FromSeconds(-1), FilmstripLayout.Snap(Time.FromSeconds(-1.2), step));
    }
}

public sealed class ThumbnailCacheTests
{
    private static BgraImage Image(int width, int height, byte fill = 7) => new(width, height, Enumerable.Repeat(fill, width * height * 4).ToArray());

    [Fact]
    public void Keys_ignore_path_case_but_not_time_or_height()
    {
        var a = new ThumbnailKey(@"C:\Videos\Toma 1.mp4", 100, 64);

        Assert.Equal(a, new ThumbnailKey(@"c:\videos\TOMA 1.MP4", 100, 64));
        Assert.Equal(a.GetHashCode(), new ThumbnailKey(@"c:\videos\TOMA 1.MP4", 100, 64).GetHashCode());
        Assert.NotEqual(a, a with { TimeTicks = 101 });
        Assert.NotEqual(a, a with { Height = 96 });
    }

    [Fact]
    public void Memory_cache_evicts_the_least_recently_used_within_its_budget()
    {
        int size = Image(10, 10).SizeBytes;
        var cache = new ThumbnailMemoryCache(size * 3);
        var keys = Enumerable.Range(0, 4).Select(i => new ThumbnailKey("a.mp4", i, 32)).ToArray();
        cache.Add(keys[0], Image(10, 10));
        cache.Add(keys[1], Image(10, 10));
        cache.Add(keys[2], Image(10, 10));
        Assert.True(cache.TryGet(keys[0], out _));

        cache.Add(keys[3], Image(10, 10));

        Assert.Equal(3, cache.Count);
        Assert.Equal(size * 3, cache.Bytes);
        Assert.True(cache.Contains(keys[0]));
        Assert.False(cache.Contains(keys[1]));
        Assert.True(cache.Contains(keys[3]));
    }

    [Fact]
    public void An_image_bigger_than_the_budget_still_stays_until_the_next_one()
    {
        var cache = new ThumbnailMemoryCache(10);
        cache.Add(new ThumbnailKey("a", 0, 32), Image(10, 10));

        Assert.Equal(1, cache.Count);
        cache.Add(new ThumbnailKey("a", 1, 32), Image(10, 10));
        Assert.Equal(1, cache.Count);
        Assert.True(cache.Contains(new ThumbnailKey("a", 1, 32)));
    }

    [Fact]
    public void Disk_names_carry_fingerprint_height_and_time()
    {
        Assert.Equal("0a1b2c-96-705600000.miniatura", ThumbnailDiskCache.FileName("0a1b2c", 96, Time.TicksPerSecond));
        var disk = new ThumbnailDiskCache(@"C:\cache\miniaturas");
        Assert.Equal(@"C:\cache\miniaturas\0a\0a1b2c-96-0.miniatura", disk.PathOf("0a1b2c", 96, 0));
    }

    [Fact]
    public void Disk_cache_round_trips_and_ignores_corrupt_files()
    {
        using var folder = new TempDirectory();
        var disk = new ThumbnailDiskCache(folder.File("miniaturas"));
        BgraImage image = Image(16, 9, 200);

        disk.Write("abcdef", 32, 1234, image);
        BgraImage? read = disk.TryRead("abcdef", 32, 1234);
        File.WriteAllBytes(disk.PathOf("abcdef", 32, 99), [1, 2, 3]);

        Assert.NotNull(read);
        Assert.Equal((16, 9), (read.Width, read.Height));
        Assert.Equal(image.Pixels, read.Pixels);
        Assert.Null(disk.TryRead("abcdef", 32, 99));
        Assert.Null(disk.TryRead("abcdef", 48, 1234));
        Assert.Null(ThumbnailDiskCache.Decode(ThumbnailDiskCache.Encode(image)[..^1]));
    }

    [Fact]
    public void Trim_deletes_the_oldest_until_under_budget()
    {
        using var folder = new TempDirectory();
        var disk = new ThumbnailDiskCache(folder.File("miniaturas"));
        for (int i = 0; i < 10; i++)
        {
            disk.Write("abcdef", 32, i, Image(32, 32));
            File.SetLastWriteTimeUtc(disk.PathOf("abcdef", 32, i), DateTime.UtcNow.AddMinutes(i - 10));
        }

        long one = new FileInfo(disk.PathOf("abcdef", 32, 0)).Length;

        Assert.Equal(0, disk.Trim(one * 10));
        int removed = disk.Trim(one * 5);

        Assert.Equal(6, removed);
        Assert.Null(disk.TryRead("abcdef", 32, 0));
        Assert.NotNull(disk.TryRead("abcdef", 32, 9));
    }
}
