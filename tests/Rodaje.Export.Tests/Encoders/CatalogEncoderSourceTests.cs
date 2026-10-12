using Rodaje.Core.Timing;
using Rodaje.Export.Encoders;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.TestMedia;

namespace Rodaje.Export.Tests.Encoders;

/// <summary>
/// Sin <c>ffmpeg.exe</c>: la fuente de codificadores de la app (<see cref="CatalogEncoderSource"/>, en
/// <c>AppExportRunner</c>) entrega el catálogo tal cual, y sus argumentos fijos llegan a la línea del sink.
/// </summary>
public sealed class CatalogEncoderSourceTests
{
    // Como en una RTX 3050: NVIDIA sí, AMD e Intel no tienen dispositivo y el AV1 de NVENC tampoco.
    private static CatalogEncoderSource Source(out FakeEncoderProbe probe)
    {
        probe = new FakeEncoderProbe(e => e.Vendor is EncoderVendor.Amd or EncoderVendor.Intel || e.Id == "av1_nvenc"
            ? ExportProbeResult.NoDevice("sin el dispositivo")
            : ExportProbeResult.Ok);
        return new CatalogEncoderSource(new ExportEncoderAvailability(probe));
    }

    [Fact]
    public async Task The_source_hands_out_the_catalog_instances_that_pass_the_probe_in_catalog_order()
    {
        IReadOnlyList<EncoderSpec> encoders = await Source(out FakeEncoderProbe probe).GetAsync();

        Assert.Equal(ExportEncoderCatalog.All.Count, probe.Calls);
        Assert.Equal(
            ExportEncoderCatalog.All.Where(e => e.Vendor is not (EncoderVendor.Amd or EncoderVendor.Intel) && e.Id != "av1_nvenc").Select(e => e.Id),
            encoders.Select(e => e.Id));
        Assert.All(encoders, e => Assert.Same(ExportEncoderCatalog.Find(e.Id), e));
    }

    [Fact]
    public async Task With_an_amd_profile_the_other_gpus_are_not_probed_and_amf_goes_first()
    {
        GpuProfile amd = new([new GpuAdapter("AMD Radeon RX 6600", 0x1002, 0x73FF, 8L << 30, 0, false, "1")], HardwareDecodeProfiles.None, 16);
        var probe = new FakeEncoderProbe(_ => ExportProbeResult.Ok);
        var source = new CatalogEncoderSource(new ExportEncoderAvailability(new GpuProfileEncoderProbe(probe, amd)), amd.PreferredEncoderVendor);

        IReadOnlyList<EncoderSpec> encoders = await source.GetAsync();
        ExportEncoderReport report = await source.Availability.GetAsync();

        Assert.Equal("h264_amf", encoders[0].Id);
        Assert.DoesNotContain(encoders, e => e.Vendor is EncoderVendor.Nvidia or EncoderVendor.Intel);
        Assert.Equal(0, probe.CallsFor("h264_nvenc") + probe.CallsFor("h264_qsv"));
        Assert.Equal(ExportProbeStatus.NoDevice, report.Find("h264_nvenc")!.Result.Status);
        Assert.False(report.HasFailures);
        Assert.Equal("h264_amf", ExportEncoderChooser.ChooseDefault(encoders)!.Encoder.Id);
    }

    [Fact]
    public async Task The_gpl_add_on_source_offers_only_x264_and_x265_out_of_the_defaults()
    {
        var probe = new FakeEncoderProbe(_ => ExportProbeResult.Ok);
        var source = new CatalogEncoderSource(new ExportEncoderAvailability(probe, catalog: GplEncoderCandidates.All));

        IReadOnlyList<EncoderSpec> encoders = await new CombinedEncoderSource(Source(out _), source).GetAsync();

        Assert.Equal(["libx264", "libx265"], encoders.TakeLast(2).Select(e => e.Id));
        Assert.All(encoders.TakeLast(2), e => Assert.False(e.InDefaults));
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public async Task Every_fixed_argument_of_the_catalog_reaches_the_sink_line()
    {
        IReadOnlyList<EncoderSpec> encoders = await Source(out _).GetAsync();
        EncoderSpec flac = encoders.Single(e => e.Id == "flac");

        foreach (EncoderSpec video in encoders.Where(e => !e.IsAudio))
        {
            List<string> line = SinkLine(video, [], flac, []);
            int codec = line.IndexOf("-c:v:0");
            Assert.True(codec >= 0 && line[codec + 1] == video.Id, string.Join(' ', line));
            Assert.True(ContainsRun(line, video.BaseArguments), $"{video.Id}: falta «{string.Join(' ', video.BaseArguments)}» en «{string.Join(' ', line)}».");
        }
    }

    [Fact]
    public async Task Ffv1_from_the_app_source_reaches_the_sink_with_one_frame_gop()
    {
        IReadOnlyList<EncoderSpec> encoders = await Source(out _).GetAsync();
        EncoderSpec ffv1 = encoders.Single(e => e.Id == "ffv1");
        EncoderSpec flac = encoders.Single(e => e.Id == "flac");
        const ExportPixelFormat pixels = ExportPixelFormat.Yuv420P;
        var settings = new ExportSettings
        {
            Container = ExportContainer.Mkv,
            Video = new VideoExportSettings
            {
                Codec = ffv1.Codec, EncoderId = ffv1.Id, RateControl = new RateControl { Mode = ffv1.RateControls[0] },
                BitDepth = pixels.BitDepth(), Chroma = pixels.Chroma(), Alpha = pixels.HasAlpha(),
            },
            Audio = new AudioExportSettings
            {
                Codec = flac.Codec, EncoderId = flac.Id, RateControl = new RateControl { Mode = flac.RateControls[0] },
            },
        };
        Assert.Empty(ExportSettingsValidator.Validate(settings, ffv1, flac));
        EncoderArguments arguments = FfmpegArguments.Build(settings, ffv1, pixels, flac, Rational.Fps30, TimeSpan.FromSeconds(2));

        List<string> line = SinkLine(ffv1, arguments.Video, flac, arguments.Audio, pixels);

        // Un solo «-g», el de FFV1, y vale 1: cada cuadro es clave.
        Assert.Single(line, a => a == "-g");
        Assert.Equal("1", line[line.IndexOf("-g") + 1]);
        Assert.Equal("3", line[line.IndexOf("-level") + 1]);
        Assert.Equal("1", line[line.IndexOf("-slicecrc") + 1]);
    }

    [Fact]
    public async Task Media_foundation_quality_goes_with_its_own_option_and_software_mft()
    {
        IReadOnlyList<EncoderSpec> encoders = await Source(out _).GetAsync();
        EncoderSpec mf = encoders.Single(e => e.Id == "h264_mf");
        EncoderSpec aac = encoders.Single(e => e.Id == "aac_mf");
        var settings = new ExportSettings
        {
            Video = new VideoExportSettings { Codec = ExportCodec.H264, EncoderId = mf.Id, RateControl = RateControl.ConstantQuality(70) },
            Audio = new AudioExportSettings { Codec = ExportCodec.Aac, EncoderId = aac.Id },
        };
        Assert.Empty(ExportSettingsValidator.Validate(settings, mf, aac));
        EncoderArguments arguments = FfmpegArguments.Build(settings, mf, ExportPixelFormat.Nv12, aac, Rational.Fps30, TimeSpan.FromSeconds(2));

        List<string> line = SinkLine(mf, arguments.Video, aac, arguments.Audio, ExportPixelFormat.Nv12, ExportContainer.Mp4);

        Assert.True(ContainsRun(line, ["-rate_control", "quality", "-quality", "70"]), string.Join(' ', line));
        Assert.True(ContainsRun(line, ["-hw_encoding", "0"]), string.Join(' ', line));
        Assert.DoesNotContain("-q:v", line);
    }

    private static List<string> SinkLine(EncoderSpec video, IReadOnlyList<string> videoArguments, EncoderSpec audio,
        IReadOnlyList<string> audioArguments, ExportPixelFormat? pixels = null, ExportContainer container = ExportContainer.Mkv)
    {
        ExportPixelFormat format = pixels ?? (video.PixelFormats.Count > 0 ? video.PixelFormats[0] : ExportPixelFormat.Yuv420P);
        string extension = container == ExportContainer.Mp4 ? "mp4" : "mkv";
        var request = new ExportSinkRequest($"salida.{extension}", container,
            new ExportVideoFormat(320, 180, Rational.Fps30, format, ExportColor.For(320, 180, format)), video, videoArguments,
            new ExportAudioFormat(48_000, 2), "mezcla.wav", audio, audioArguments, 60);
        return [.. FfmpegPipeArguments.Build(request, $"salida.tmp.{extension}", null)];
    }

    private static bool ContainsRun(List<string> line, IReadOnlyList<string> run)
    {
        for (int i = 0; i + run.Count <= line.Count; i++)
        {
            if (line.Skip(i).Take(run.Count).SequenceEqual(run, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return run.Count == 0;
    }
}
