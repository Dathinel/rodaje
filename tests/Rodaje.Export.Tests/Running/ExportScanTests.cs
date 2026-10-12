using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Export.Encoders;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.TestMedia;

namespace Rodaje.Export.Tests.Running;

/// <summary>
/// Entrelazado (se compone por campos y FFmpeg los teje) y video 360 (metadatos esféricos añadidos al MP4/MOV), sin GPU:
/// el plan, los argumentos, la validación y el editor de cajas sobre archivos que escribe el ffmpeg.exe de verdad.
/// </summary>
public sealed class ExportScanTests : IDisposable
{
    private static readonly IReadOnlyList<EncoderSpec> Encoders = [.. ExportEncoderCatalog.All.Where(e => e.Vendor is not (EncoderVendor.Amd or EncoderVendor.Intel))];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rodaje-pruebas-360", "carpeta ñ " + Guid.NewGuid().ToString("N")[..8]);

    private static ExportSettings Interlaced(ExportFieldOrder order) => new()
    {
        Container = ExportContainer.Mov,
        Video = new VideoExportSettings
        {
            Codec = ExportCodec.ProRes, Profile = "hq", BitDepth = 10, Chroma = ChromaSubsampling.Chroma422,
            RateControl = RateControl.ProfileDefined, FrameRate = new Rational(25, 1), FieldOrder = order,
        },
        Audio = new AudioExportSettings { Codec = ExportCodec.Pcm, RateControl = RateControl.Lossless, Loudness = LoudnessTarget.EbuR128 },
        FastStart = false,
    };

    [Fact]
    public void Interlaced_composes_at_the_field_rate_and_weaves_pairs_of_fields()
    {
        ExportPlan plan = ExportPlan.Create(ExportPlanTests.Request(ExportPlanTests.Project(Time.FromSeconds(2)), Interlaced(ExportFieldOrder.TopFirst)), Encoders);

        Assert.True(plan.Interlaced);
        Assert.Equal(new Rational(50, 1), plan.OutputRate);
        Assert.Equal(new Rational(25, 1), plan.EncodedRate);
        Assert.Equal((0L, 100L, 50L), (plan.FirstFrame, plan.FrameCount, plan.EncodedFrameCount));
        Assert.Equal(Time.FromSeconds(2), plan.End);
        Assert.Equal(["setfield=tff", "separatefields", @"select=eq(mod(n\,4)\,0)+eq(mod(n\,4)\,3)", "weave=first_field=top", "setpts=N*1/(25*TB)", "setfield=tff", "format=yuv422p10le"],
            plan.VideoFilters);
        Assert.Contains("+ildct", plan.Arguments.Video);
        ExportExpectation expected = plan.Expectation()!;
        Assert.Equal((50L, new Rational(25, 1)), (expected.FrameCount!.Value, expected.FrameRate!.Value));
    }

    [Fact]
    public void Bottom_field_first_takes_the_bottom_field_of_the_even_frame()
    {
        ExportPlan plan = ExportPlan.Create(ExportPlanTests.Request(ExportPlanTests.Project(Time.FromSeconds(1)), Interlaced(ExportFieldOrder.BottomFirst)), Encoders);

        Assert.Equal("setfield=bff", plan.VideoFilters[0]);
        Assert.Equal("weave=first_field=bottom", plan.VideoFilters[3]);
    }

    [Fact]
    public void Interlaced_and_360_reject_what_cannot_carry_them()
    {
        ExportSettings h264 = new() { Video = new VideoExportSettings { Codec = ExportCodec.H264, FrameRate = new Rational(25, 1), FieldOrder = ExportFieldOrder.TopFirst } };
        Assert.Contains(Validate(h264), p => p.Message.Contains("entrelazado", StringComparison.Ordinal));
        ExportSettings noRate = Interlaced(ExportFieldOrder.TopFirst) with { Video = Interlaced(ExportFieldOrder.TopFirst).Video! with { FrameRate = null } };
        Assert.Contains(Validate(noRate), p => p.Message.Contains("tasa de cuadros fija", StringComparison.Ordinal));
        ExportSettings sphericalMkv = new()
        {
            Container = ExportContainer.Mkv,
            Video = new VideoExportSettings { Codec = ExportCodec.H264, Width = 3840, Height = 1920, Projection = ExportProjection.Equirectangular },
        };
        Assert.Contains(Validate(sphericalMkv), p => p.Message.Contains("MP4 o MOV", StringComparison.Ordinal));
        ExportSettings notTwoToOne = sphericalMkv with { Container = ExportContainer.Mp4, Video = sphericalMkv.Video! with { Height = 2160 } };
        Assert.Contains(Validate(notTwoToOne), p => p.Message.Contains("2:1", StringComparison.Ordinal));
        Assert.Empty(Validate(sphericalMkv with { Container = ExportContainer.Mp4 }));
    }

    [Fact]
    public void Interlaced_output_is_never_copied_directly()
    {
        ExportPlan plan = ExportPlan.Create(ExportPlanTests.Request(ExportPlanTests.Project(Time.FromSeconds(1)), Interlaced(ExportFieldOrder.TopFirst)), Encoders);

        StreamCopyDecision decision = StreamCopyPlanner.Decide(plan);

        Assert.Null(decision.Copy);
        Assert.Contains("entrelazada", decision.Explanation, StringComparison.Ordinal);
    }

    [FfmpegFact]
    public async Task The_weave_filters_give_an_interlaced_prores_with_half_the_frames()
    {
        string path = Path.Combine(Directory.CreateDirectory(_folder).FullName, "1080i.mov");
        IReadOnlyList<string> filters = ExportPlan.WeaveFields(ExportFieldOrder.TopFirst, ExportPixelFormat.Yuv422P10, new Rational(25, 1));

        await FfmpegAsync("-f", "lavfi", "-i", "testsrc2=size=320x240:rate=50:duration=2", "-c:v", "prores_ks", "-profile:v", "hq", "-flags", "+ildct",
            "-filter:v:0", string.Join(',', filters), path);

        string probe = await ProbeAsync("-select_streams", "v:0", "-count_frames", "-show_entries", "stream=field_order,r_frame_rate,nb_read_frames", "-of", "compact", path);
        Assert.Contains("field_order=tb", probe, StringComparison.Ordinal);
        Assert.Contains("r_frame_rate=25/1", probe, StringComparison.Ordinal);
        Assert.Contains("nb_read_frames=50", probe, StringComparison.Ordinal);
    }

    [FfmpegFact]
    public async Task Spherical_boxes_go_into_an_mp4_with_moov_at_the_start_and_it_still_decodes()
    {
        string path = await EncodeAsync("faststart.mp4", "+faststart");

        SphericalMetadata.Inject(path);
        SphericalMetadata.Inject(path);

        Assert.True(SphericalMetadata.IsEquirectangular(path));
        string probe = await ProbeAsync("-select_streams", "v:0", "-show_streams", path);
        Assert.Contains("equirectangular", probe, StringComparison.Ordinal);
        Assert.Equal("", await FfmpegAsync("-v", "error", "-i", path, "-f", "null", "-"));
        string frames = await ProbeAsync("-select_streams", "v:0", "-count_frames", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", path);
        Assert.Equal("48", frames.Trim().TrimEnd(','));
    }

    [FfmpegFact]
    public async Task Spherical_boxes_go_into_a_mov_with_moov_at_the_end_in_place()
    {
        string path = await EncodeAsync("final.mov", "");
        long before = new FileInfo(path).Length;

        SphericalMetadata.Inject(path);

        Assert.True(SphericalMetadata.IsEquirectangular(path));
        Assert.Equal(before + 100, new FileInfo(path).Length);
        Assert.Contains("equirectangular", await ProbeAsync("-select_streams", "v:0", "-show_streams", path), StringComparison.Ordinal);
        Assert.Equal("", await FfmpegAsync("-v", "error", "-i", path, "-f", "null", "-"));
    }

    [Fact]
    public void A_file_that_is_not_mp4_is_rejected_without_changes()
    {
        string path = Path.Combine(Directory.CreateDirectory(_folder).FullName, "no.mp4");
        File.WriteAllBytes(path, [0, 0, 0, 8, (byte)'f', (byte)'r', (byte)'e', (byte)'e']);

        Assert.Throws<InvalidDataException>(() => SphericalMetadata.Inject(path));
        Assert.Equal(8, new FileInfo(path).Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static IReadOnlyList<ExportProblem> Validate(ExportSettings settings) => ExportSettingsValidator.Validate(settings,
        settings.Video is { } v ? Encoders.First(e => e.Codec == v.Codec && e.InDefaults) : null,
        settings.Audio is { } a ? Encoders.First(e => e.Codec == a.Codec && e.InDefaults) : null);

    private async Task<string> EncodeAsync(string name, string movflags)
    {
        string path = Path.Combine(Directory.CreateDirectory(_folder).FullName, name);
        List<string> args = ["-f", "lavfi", "-i", "testsrc2=size=256x128:rate=24:duration=2", "-f", "lavfi", "-i", "sine=duration=2",
            "-c:v", "mpeg4", "-c:a", "aac", "-shortest"];
        if (movflags.Length > 0)
        {
            args.AddRange(["-movflags", movflags]);
        }

        await FfmpegAsync([.. args, path]);
        return path;
    }

    private static Task<string> FfmpegAsync(params string[] args) => RunAsync(Skip.ProxyExecutable!.Path, ["-hide_banner", "-loglevel", "error", "-y", .. args]);

    private static Task<string> ProbeAsync(params string[] args) =>
        RunAsync(Path.Combine(Path.GetDirectoryName(Skip.ProxyExecutable!.Path)!, "ffprobe.exe"), ["-v", "error", .. args]);

    private static async Task<string> RunAsync(string exe, string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        using Process process = Process.Start(info)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return output + await error;
    }
}
