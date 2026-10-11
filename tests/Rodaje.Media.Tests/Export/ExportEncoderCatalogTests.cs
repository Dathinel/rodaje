using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

public sealed class ExportEncoderCatalogTests
{
    [Fact]
    public void Ids_are_unique()
    {
        string[] ids = [.. ExportEncoderCatalog.All.Select(e => e.Id)];

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Hardware_comes_before_software()
    {
        bool[] flags = [.. ExportEncoderCatalog.All.Select(e => e.IsHardware)];
        int firstSoftware = Array.IndexOf(flags, false);

        Assert.True(firstSoftware > 0);
        Assert.All(flags.Skip(firstSoftware), hardware => Assert.False(hardware));
        Assert.All(flags.Take(firstSoftware), Assert.True);
    }

    [Theory]
    [InlineData("libx264")]
    [InlineData("libx265")]
    [InlineData("libx264rgb")]
    public void Gpl_encoders_are_not_in_the_catalog(string id) => Assert.Null(ExportEncoderCatalog.Find(id));

    [Fact]
    public void No_encoder_mentions_gpl_or_x26x()
    {
        foreach (ExportEncoderInfo encoder in ExportEncoderCatalog.All)
        {
            Assert.DoesNotContain("x264", encoder.Id, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("x265", encoder.Id, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("GPL", encoder.License.Replace("LGPL", "", StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_encoder_in_the_catalog_is_compiled_into_the_lgpl_build()
    {
        string configure = File.ReadAllText(FindRepoFile("docs", "ffmpeg-configure.txt"));
        var software = new Dictionary<string, string>
        {
            ["libsvtav1"] = "--enable-libsvtav1",
            ["libaom-av1"] = "--enable-libaom",
            ["libvpx-vp9"] = "--enable-libvpx",
            ["libkvazaar"] = "--enable-libkvazaar",
            ["libopenh264"] = "--enable-libopenh264",
        };
        foreach (ExportEncoderInfo encoder in ExportEncoderCatalog.All.Where(e => !e.IsHardware))
        {
            Assert.Contains(software[encoder.Id], configure, StringComparison.Ordinal);
        }

        Assert.Equal(software.Count, ExportEncoderCatalog.All.Count(e => !e.IsHardware));
    }

    [Fact]
    public void Software_h264_and_hevc_are_marked_as_patent_encumbered_and_hardware_is_not()
    {
        foreach (ExportEncoderInfo encoder in ExportEncoderCatalog.All)
        {
            bool expected = !encoder.IsHardware && encoder.Codec is ExportCodec.H264 or ExportCodec.Hevc;
            Assert.Equal(expected, encoder.PatentEncumbered);
        }

        Assert.True(ExportEncoderCatalog.Find("libopenh264")!.LowQuality);
    }

    public static TheoryData<string> AllIds()
    {
        var data = new TheoryData<string>();
        foreach (ExportEncoderInfo encoder in ExportEncoderCatalog.All)
        {
            data.Add(encoder.Id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllIds))]
    public void Every_encoder_builds_arguments_for_every_quality_and_speed(string id)
    {
        ExportEncoderInfo encoder = ExportEncoderCatalog.Find(id)!;
        foreach (ExportQuality quality in Enum.GetValues<ExportQuality>())
        {
            foreach (ExportSpeed speed in Enum.GetValues<ExportSpeed>())
            {
                var args = encoder.BaseArguments(quality, speed);
                Assert.NotEmpty(args);
                Assert.All(args, a => Assert.False(string.IsNullOrWhiteSpace(a)));
                Assert.DoesNotContain("-c:v", args);
            }
        }
    }

    [Fact]
    public void Higher_quality_lowers_the_quantizer_and_slower_speed_picks_a_slower_preset()
    {
        ExportEncoderInfo svt = ExportEncoderCatalog.Find("libsvtav1")!;

        static int ValueAfter(System.Collections.Immutable.ImmutableArray<string> args, string flag) =>
            int.Parse(args[args.IndexOf(flag) + 1], System.Globalization.CultureInfo.InvariantCulture);

        int Crf(ExportQuality q) => ValueAfter(svt.BaseArguments(q, ExportSpeed.Balanced), "-crf");
        int Preset(ExportSpeed s) => ValueAfter(svt.BaseArguments(ExportQuality.Balanced, s), "-preset");

        Assert.True(Crf(ExportQuality.Draft) > Crf(ExportQuality.Balanced));
        Assert.True(Crf(ExportQuality.Balanced) > Crf(ExportQuality.High));
        Assert.True(Preset(ExportSpeed.Fast) > Preset(ExportSpeed.Balanced));
        Assert.True(Preset(ExportSpeed.Balanced) > Preset(ExportSpeed.Slow));
    }

    [Fact]
    public void Arguments_with_an_undefined_quality_are_rejected()
    {
        ExportEncoderInfo encoder = ExportEncoderCatalog.All[0];

        Assert.Throws<ArgumentOutOfRangeException>(() => encoder.BaseArguments((ExportQuality)99, ExportSpeed.Fast));
        Assert.Throws<ArgumentOutOfRangeException>(() => encoder.BaseArguments(ExportQuality.Draft, (ExportSpeed)99));
    }

    [Fact]
    public void An_unknown_encoder_has_no_arguments()
    {
        var unknown = new ExportEncoderInfo("libx264", "x264", ExportCodec.H264, ExportVendor.None, "GPL-2.0", true, false, "yuv420p");

        Assert.Throws<ArgumentException>(() => unknown.BaseArguments(ExportQuality.Balanced, ExportSpeed.Balanced));
    }

    internal static string FindRepoFile(params string[] parts)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
            {
                return Path.Combine([dir.FullName, .. parts]);
            }
        }

        throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }
}
