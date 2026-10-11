using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

public sealed class ExportEncoderCatalogTests
{
    public static TheoryData<string> AllIds()
    {
        var data = new TheoryData<string>();
        foreach (EncoderSpec encoder in ExportEncoderCatalog.All)
        {
            data.Add(encoder.Id);
        }

        return data;
    }

    [Fact]
    public void Ids_are_unique_and_all_is_video_then_audio()
    {
        string[] ids = [.. ExportEncoderCatalog.All.Select(e => e.Id)];

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal([.. ExportEncoderCatalog.Video, .. ExportEncoderCatalog.Audio], ExportEncoderCatalog.All);
        Assert.All(ExportEncoderCatalog.Video, e => Assert.False(e.IsAudio));
        Assert.All(ExportEncoderCatalog.Audio, e => Assert.True(e.IsAudio));
    }

    [Fact]
    public void The_gpu_comes_first_then_media_foundation_then_patent_free_software()
    {
        string[] head = [.. ExportEncoderCatalog.Video.Take(14).Select(e => e.Id)];

        Assert.Equal(
            ["h264_nvenc", "hevc_nvenc", "av1_nvenc", "h264_amf", "hevc_amf", "av1_amf", "h264_qsv", "hevc_qsv", "av1_qsv",
                "h264_mf", "hevc_mf", "libsvtav1", "libaom-av1", "libvpx-vp9"],
            head);
        Assert.All(ExportEncoderCatalog.Video.Take(9), e => Assert.True(e.IsHardware));
        Assert.All(ExportEncoderCatalog.All.Skip(9), e => Assert.False(e.IsHardware));
    }

    [Theory]
    [InlineData("libx264")]
    [InlineData("libx265")]
    [InlineData("libx264rgb")]
    public void Gpl_encoders_are_not_in_the_catalog(string id) => Assert.Null(ExportEncoderCatalog.Find(id));

    [Fact]
    public void Nothing_in_the_catalog_is_gpl()
    {
        Assert.All(ExportEncoderCatalog.All, e => Assert.NotEqual(EncoderLicense.GplAddOn, e.License));
        Assert.All(ExportEncoderCatalog.All, e => Assert.DoesNotContain("x26", e.Id, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_external_library_is_enabled_in_the_lgpl_build()
    {
        string configure = File.ReadAllText(FindRepoFile("docs", "ffmpeg-configure.txt"));
        var libraries = new Dictionary<string, string>
        {
            ["libsvtav1"] = "--enable-libsvtav1",
            ["libaom-av1"] = "--enable-libaom",
            ["libvpx-vp9"] = "--enable-libvpx",
            ["libkvazaar"] = "--enable-libkvazaar",
            ["libopenh264"] = "--enable-libopenh264",
            ["libwebp"] = "--enable-libwebp",
            ["libopus"] = "--enable-libopus",
            ["libmp3lame"] = "--enable-libmp3lame",
        };

        Assert.Equal(libraries.Keys.Order(StringComparer.Ordinal),
            ExportEncoderCatalog.All.Where(e => e.Id.StartsWith("lib", StringComparison.Ordinal)).Select(e => e.Id).Order(StringComparer.Ordinal));
        foreach ((string id, string flag) in libraries)
        {
            Assert.True(configure.Contains(flag, StringComparison.Ordinal), $"{id}: falta {flag} en docs/ffmpeg-configure.txt.");
        }

        Assert.All(new[] { "--enable-amf", "--enable-ffnvcodec", "--enable-libvpl" }, flag => Assert.Contains(flag, configure, StringComparison.Ordinal));
    }

    [Fact]
    public void Only_kvazaar_and_openh264_stay_out_of_the_defaults_and_they_say_why()
    {
        Assert.Equal(["libkvazaar", "libopenh264"], ExportEncoderCatalog.All.Where(e => !e.InDefaults).Select(e => e.Id));
        foreach (string id in new[] { "libkvazaar", "libopenh264" })
        {
            Assert.StartsWith("Patentes de H.264/HEVC sin cubrir por software: no por defecto.", ExportEncoderCatalog.Find(id)!.Note, StringComparison.Ordinal);
        }

        Assert.Contains("calidad baja", ExportEncoderCatalog.Find("libopenh264")!.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Automatic_h264_and_hevc_only_come_from_the_gpu_or_windows()
    {
        // D-028: H.264 y HEVC solo con la licencia de patentes de la GPU o de Windows.
        Assert.All(ExportEncoderCatalog.All.Where(e => e.InDefaults && e.Codec is ExportCodec.H264 or ExportCodec.Hevc),
            e => Assert.True(e.IsHardware || e.Vendor == EncoderVendor.Microsoft, e.Id));
    }

    [Fact]
    public void Media_foundation_asks_for_the_software_mft()
    {
        foreach (string id in new[] { "h264_mf", "hevc_mf" })
        {
            EncoderSpec mf = ExportEncoderCatalog.Find(id)!;
            Assert.False(mf.IsHardware);
            Assert.Equal(EncoderVendor.Microsoft, mf.Vendor);
            Assert.Equal(["-hw_encoding", "0"], mf.BaseArguments);
        }
    }

    [Fact]
    public void Gpu_encoders_take_nv12_and_ten_bit_p010()
    {
        foreach (EncoderSpec gpu in ExportEncoderCatalog.All.Where(e => e.IsHardware || e.Id.EndsWith("_mf", StringComparison.Ordinal) && !e.IsAudio))
        {
            Assert.Equal(ExportPixelFormat.Nv12, gpu.PixelFormats[0]);
            Assert.DoesNotContain(ExportPixelFormat.Yuv420P, gpu.PixelFormats);
            Assert.Equal(gpu.Codec is not ExportCodec.H264 && gpu.Vendor != EncoderVendor.Microsoft, gpu.PixelFormats.Contains(ExportPixelFormat.P010));
        }

        foreach (string id in new[] { "libsvtav1", "libaom-av1", "libvpx-vp9" })
        {
            EncoderSpec encoder = ExportEncoderCatalog.Find(id)!;
            Assert.Equal([ExportPixelFormat.Yuv420P, ExportPixelFormat.Yuv420P10], encoder.PixelFormats.Take(2));
        }
    }

    [Fact]
    public void Av1_nvenc_says_it_needs_an_rtx_40()
    {
        Assert.Contains("RTX 40", ExportEncoderCatalog.Find("av1_nvenc")!.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Amd_and_intel_are_marked_as_untested()
    {
        Assert.All(ExportEncoderCatalog.All.Where(e => e.Vendor is EncoderVendor.Amd or EncoderVendor.Intel),
            e => Assert.Contains("Sin probar", e.Note, StringComparison.Ordinal));
    }

    [Fact]
    public void Kvazaar_and_openh264_never_get_a_crf_or_a_preset()
    {
        // -crf no existe en libkvazaar (va en -kvazaar-params) y OpenH264 ignora calidad y velocidad.
        foreach (string id in new[] { "libkvazaar", "libopenh264" })
        {
            EncoderSpec encoder = ExportEncoderCatalog.Find(id)!;
            Assert.Null(encoder.Quality);
            Assert.False(encoder.Supports(RateControlMode.ConstantQuality));
            Assert.Empty(encoder.SpeedPresets);
            Assert.DoesNotContain("-crf", encoder.BaseArguments);
        }

        Assert.Equal(["-kvazaar-params", "preset=medium"], ExportEncoderCatalog.Find("libkvazaar")!.BaseArguments);
    }

    [Theory]
    [InlineData("libaom-av1")]
    [InlineData("libvpx-vp9")]
    [InlineData("libkvazaar")]
    [InlineData("libopenh264")]
    [InlineData("libwebp")]
    public void Encoders_without_a_speed_preset_option_list_none(string id)
    {
        // FfmpegArguments pone -preset con lo que diga SpeedPresets; estos no tienen esa opción (o significa otra cosa).
        EncoderSpec encoder = ExportEncoderCatalog.Find(id)!;

        Assert.Empty(encoder.SpeedPresets);
        Assert.Null(encoder.DefaultSpeedPreset);
    }

    [Theory]
    [MemberData(nameof(AllIds))]
    public void Every_encoder_is_coherent(string id)
    {
        EncoderSpec encoder = ExportEncoderCatalog.Find(id)!;

        Assert.NotEmpty(encoder.RateControls);
        Assert.Equal(encoder.Supports(RateControlMode.ConstantQuality), encoder.Quality is not null);
        if (encoder.Quality is { } scale)
        {
            Assert.True(scale.Min < scale.Max);
            Assert.True(scale.Contains(scale.Default));
            Assert.StartsWith("-", scale.Argument, StringComparison.Ordinal);
        }

        if (encoder.DefaultSpeedPreset is { } preset)
        {
            Assert.Contains(preset, encoder.SpeedPresets);
        }

        Assert.All(encoder.BaseArguments, a => Assert.False(string.IsNullOrWhiteSpace(a)));
        Assert.DoesNotContain("-c:v", encoder.BaseArguments);
        Assert.DoesNotContain("-pix_fmt", encoder.BaseArguments);
        if (encoder.IsAudio)
        {
            Assert.Empty(encoder.PixelFormats);
            Assert.True(encoder.MaxChannels > 0);
        }
        else
        {
            Assert.NotEmpty(encoder.PixelFormats);
            Assert.Equal(0, encoder.MaxChannels);
            Assert.Empty(encoder.SampleRates);
            Assert.Equal(encoder.PixelFormats.Count, encoder.PixelFormats.Distinct().Count());
        }
    }

    [Theory]
    [MemberData(nameof(AllIds))]
    public void Profiles_only_name_formats_of_their_encoder_and_cover_every_format(string id)
    {
        EncoderSpec encoder = ExportEncoderCatalog.Find(id)!;
        if (encoder.Profiles.Count == 0)
        {
            return;
        }

        Assert.All(encoder.Profiles, p => Assert.All(p.PixelFormats, f => Assert.Contains(f, encoder.PixelFormats)));

        // Con perfiles, cada formato tiene uno: el primero que lo admite es el de por defecto (FfmpegArguments y la sonda).
        Assert.All(encoder.PixelFormats, f => Assert.Contains(encoder.Profiles, p => p.Allows(f)));
    }

    [Fact]
    public void Every_codec_of_the_contract_has_an_encoder()
    {
        Assert.All(Enum.GetValues<ExportCodec>(), codec => Assert.Contains(ExportEncoderCatalog.All, e => e.Codec == codec));
    }

    [Theory]
    [MemberData(nameof(AllIds))]
    public void Every_encoder_fits_some_container(string id)
    {
        EncoderSpec encoder = ExportEncoderCatalog.Find(id)!;

        Assert.Contains(Enum.GetValues<ExportContainer>(), c => c.Allows(encoder.Codec));
    }

    [Fact]
    public void The_masters_and_audio_of_the_queue_are_there()
    {
        string[] expected =
        [
            "ffv1", "dnxhd", "prores_ks", "mjpeg", "png", "qtrle", "gif", "libwebp", "tiff", "cfhd",
            "aac_mf", "aac", "libopus", "libmp3lame", "flac", "alac", "pcm_s16le", "pcm_s24le", "pcm_f32le",
        ];

        Assert.All(expected, id => Assert.NotNull(ExportEncoderCatalog.Find(id)));
        Assert.True(ExportEncoderCatalog.IndexOf("aac_mf") < ExportEncoderCatalog.IndexOf("aac"));
        Assert.Equal([44_100, 48_000], ExportEncoderCatalog.Find("aac_mf")!.SampleRates);
        Assert.Equal(["dnxhr_hq", "dnxhr_lb", "dnxhr_sq", "dnxhr_hqx", "dnxhr_444"], ExportEncoderCatalog.Find("dnxhd")!.Profiles.Select(p => p.Id));
        Assert.Equal(["hq", "proxy", "lt", "standard", "4444", "4444xq"], ExportEncoderCatalog.Find("prores_ks")!.Profiles.Select(p => p.Id));
        Assert.True(ExportEncoderCatalog.Find("prores_ks")!.SupportsAlpha);
        Assert.True(ExportEncoderCatalog.Find("qtrle")!.SupportsAlpha);
    }

    [Fact]
    public void Display_names_and_positions()
    {
        Assert.Equal("SVT-AV1 (AV1)", ExportEncoderCatalog.DisplayName(ExportEncoderCatalog.Find("libsvtav1")!));
        Assert.All(ExportEncoderCatalog.All, e => Assert.NotEqual(e.Id, ExportEncoderCatalog.DisplayName(e)));
        Assert.Equal(0, ExportEncoderCatalog.IndexOf("h264_nvenc"));
        Assert.Equal(int.MaxValue, ExportEncoderCatalog.IndexOf("libx264"));
        Assert.Equal("otro", ExportEncoderCatalog.DisplayName(ExportEncoderCatalog.Find("png")! with { Id = "otro" }));
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
