using Rodaje.Core.Timing;
using Rodaje.Export.Tests.Support;
using Rodaje.Media.Export;
using Rodaje.Media.Proxies;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Export.Tests.Output;

/// <summary>De <see cref="ExportSettings"/> a la línea de comandos, y exportaciones reales con esos argumentos.</summary>
public sealed class FfmpegArgumentsTests(ITestOutputHelper output)
{
    private static readonly Rational Rate = Rational.Fps30;

    [Fact]
    public void Nvenc_quality_profile_gop_and_two_passes_in_one()
    {
        var video = new VideoExportSettings
        {
            Codec = ExportCodec.H264,
            RateControl = RateControl.ConstantQuality(21) with { Passes = 2 },
            Level = "4.1",
            Tune = "hq",
            Gop = new GopSettings { KeyframeSeconds = 2, BFrames = 9, References = 3 },
        };

        IReadOnlyList<string> args = FfmpegArguments.Video(video, TestEncoders.H264Nvenc, ExportPixelFormat.Nv12, Rate, TimeSpan.FromSeconds(10), 192);

        Assert.Equal(
            ["-rc", "vbr", "-cq", "21", "-b:v", "0", "-multipass", "fullres", "-profile:v", "high", "-level:v", "4.1", "-preset", "p4",
                "-tune", "hq", "-g", "60", "-flags", "+cgop", "-bf", "4", "-refs", "3"],
            args);
        Assert.Equal(1, FfmpegArguments.PassCount(video.RateControl, TestEncoders.H264Nvenc));
    }

    [Theory]
    [InlineData(RateControlMode.Vbr, "-rc vbr -b:v 8000k -maxrate 12000k -bufsize 24000k")]
    [InlineData(RateControlMode.Cbr, "-rc cbr -b:v 8000k -maxrate 8000k -bufsize 16000k")]
    [InlineData(RateControlMode.Abr, "-rc vbr -b:v 8000k")]
    public void Nvenc_bitrate_modes(RateControlMode mode, string expected)
    {
        var rate = new RateControl { Mode = mode, BitrateKbps = 8000, MaxBitrateKbps = mode == RateControlMode.Vbr ? 12000 : null };
        var video = new VideoExportSettings { Codec = ExportCodec.H264, RateControl = rate, Gop = new GopSettings { KeyframeSeconds = null } };

        IReadOnlyList<string> args = FfmpegArguments.Video(video, TestEncoders.H264Nvenc, ExportPixelFormat.Nv12, Rate, TimeSpan.FromSeconds(10), 0);

        Assert.StartsWith(expected + " -profile:v high", string.Join(' ', args), StringComparison.Ordinal);
    }

    [Fact]
    public void Software_encoders_use_their_own_quality_scale()
    {
        var crf = new VideoExportSettings { Codec = ExportCodec.Av1, RateControl = RateControl.ConstantQuality(30), BitDepth = 10, Tune = "0" };
        var vp9 = new VideoExportSettings { Codec = ExportCodec.Vp9, RateControl = RateControl.ConstantQuality(33), Gop = new GopSettings { Closed = true } };

        string svt = string.Join(' ', FfmpegArguments.Video(crf, TestEncoders.SvtAv1, ExportPixelFormat.Yuv420P10, Rate, TimeSpan.FromSeconds(1), 0));
        string vpx = string.Join(' ', FfmpegArguments.Video(vp9, Vp9, ExportPixelFormat.Yuv420P, Rate, TimeSpan.FromSeconds(1), 0));

        Assert.Equal("-crf 30 -svtav1-params tune=0 -g 60 -flags +cgop", svt);
        Assert.Equal("-crf 33 -b:v 0 -g 60", vpx);
    }

    [Fact]
    public void Media_foundation_rate_modes()
    {
        EncoderSpec mf = TestEncoders.H264Nvenc with { Id = "h264_mf", Quality = new("Calidad", "-quality", 0, 100, 60, LowerIsBetter: false) };
        var quality = new VideoExportSettings { Codec = ExportCodec.H264, RateControl = RateControl.ConstantQuality(70), Gop = new GopSettings { KeyframeSeconds = null, Closed = false } };
        var vbr = quality with { RateControl = RateControl.Vbr(6000, 9000) };

        Assert.Equal("-rate_control quality -quality 70", Head(FfmpegArguments.Video(quality, mf, ExportPixelFormat.Nv12, Rate, TimeSpan.FromSeconds(1), 0), 4));
        Assert.Equal("-rate_control pc_vbr -b:v 6000k -maxrate 9000k -bufsize 18000k",
            Head(FfmpegArguments.Video(vbr, mf, ExportPixelFormat.Nv12, Rate, TimeSpan.FromSeconds(1), 0), 8));
    }

    [Fact]
    public void Target_size_becomes_an_average_bitrate()
    {
        var video = new VideoExportSettings { Codec = ExportCodec.Vp9, RateControl = RateControl.TargetSize(10_000_000) };

        IReadOnlyList<string> args = FfmpegArguments.Video(video, Vp9, ExportPixelFormat.Yuv420P, Rate, TimeSpan.FromSeconds(60), 128);

        // 10 MB en 60 s: 1306 kbps en total menos el 2 % del contenedor y 128 de audio.
        Assert.Equal(["-b:v", "1178k"], args.Take(2));
        Assert.Equal(2, FfmpegArguments.PassCount(video.RateControl, Vp9));
    }

    [Fact]
    public void Intra_only_codecs_get_no_gop_and_alpha_gets_its_bits()
    {
        var prores = new VideoExportSettings { Codec = ExportCodec.ProRes, RateControl = RateControl.ProfileDefined, Profile = "4444", BitDepth = 10, Chroma = ChromaSubsampling.Chroma444, Alpha = true };

        IReadOnlyList<string> args = FfmpegArguments.Video(prores, TestEncoders.ProRes, ExportPixelFormat.Yuva444P10, Rate, TimeSpan.FromSeconds(1), 0);

        Assert.Equal(["-profile:v", "4444", "-alpha_bits", "16"], args);
    }

    [Fact]
    public void The_default_profile_is_the_first_that_takes_the_pixels()
    {
        var hevc = new VideoExportSettings { Codec = ExportCodec.Hevc, BitDepth = 10, Gop = new GopSettings { KeyframeSeconds = null, Closed = false } };

        IReadOnlyList<string> args = FfmpegArguments.Video(hevc, TestEncoders.HevcNvenc, ExportPixelFormat.P010, Rate, TimeSpan.FromSeconds(1), 0);

        Assert.Contains("main10", args);
    }

    [Theory]
    [InlineData("aac_mf", RateControlMode.Cbr, 170, null, "-b:a 160k -ar 48000 -ac 2")]
    [InlineData("libopus", RateControlMode.Vbr, 128, null, "-b:a 128k -vbr on -ar 48000 -ac 2")]
    [InlineData("libopus", RateControlMode.Cbr, 96, null, "-b:a 96k -vbr off -ar 48000 -ac 2")]
    [InlineData("libmp3lame", RateControlMode.Vbr, null, 2.0, "-q:a 2 -ar 48000 -ac 2")]
    [InlineData("libmp3lame", RateControlMode.Abr, 192, null, "-b:a 192k -abr 1 -ar 48000 -ac 2")]
    [InlineData("flac", RateControlMode.Lossless, null, null, "-sample_fmt s32 -bits_per_raw_sample 24 -ar 48000 -ac 2")]
    [InlineData("alac", RateControlMode.Lossless, null, null, "-sample_fmt s32p -ar 48000 -ac 2")]
    [InlineData("pcm_s24le", RateControlMode.Lossless, null, null, "-ar 48000 -ac 2")]
    [InlineData("aac", RateControlMode.Cbr, 256, null, "-b:a 256k -ar 48000 -ac 2")]
    public void Audio_arguments_per_encoder(string id, RateControlMode mode, int? kbps, double? quality, string expected)
    {
        EncoderSpec encoder = TestEncoders.Pcm24 with { Id = id, Codec = id switch { "libopus" => ExportCodec.Opus, "libmp3lame" => ExportCodec.Mp3, "flac" => ExportCodec.Flac, "alac" => ExportCodec.Alac, "pcm_s24le" => ExportCodec.Pcm, _ => ExportCodec.Aac } };
        var audio = new AudioExportSettings { Codec = encoder.Codec, RateControl = new RateControl { Mode = mode, BitrateKbps = kbps, Quality = quality } };

        Assert.Equal(expected, string.Join(' ', FfmpegArguments.Audio(audio, encoder)));
    }

    [Fact]
    public void Loudness_profile_and_container_flags()
    {
        var settings = new ExportSettings
        {
            Container = ExportContainer.Mp4,
            Video = new VideoExportSettings { Codec = ExportCodec.Hevc },
            Audio = new AudioExportSettings { Codec = ExportCodec.Aac, Profile = "aac_low", SampleRate = 44_100 },
        };

        EncoderArguments args = FfmpegArguments.Build(settings, TestEncoders.HevcNvenc, null, TestEncoders.AacMf, Rate, TimeSpan.FromSeconds(5), "loudnorm=I=-14");

        Assert.EndsWith("-tag:v hvc1 -movflags +faststart", string.Join(' ', args.Video), StringComparison.Ordinal);
        Assert.Equal("-b:a 192k -profile:a aac_low -af loudnorm=I=-14 -ar 44100 -ac 2", string.Join(' ', args.Audio));
        Assert.Equal(["-movflags", "+faststart"], FfmpegArguments.Build(settings with { Video = null }, null, null, TestEncoders.AacMf, Rate, TimeSpan.FromSeconds(5)).Audio.TakeLast(2));
        Assert.Empty(FfmpegArguments.Container(settings with { FastStart = false }));
    }

    [ProxyFact]
    public void Youtube_like_h264_keeps_profile_gop_and_faststart()
    {
        FfmpegExecutable ffmpeg = Skip.ProxyExecutable!;
        if (ffmpeg.ProbeEncoder("h264_nvenc") is { } missing)
        {
            output.WriteLine(missing);
            return;
        }

        var settings = new ExportSettings
        {
            Container = ExportContainer.Mp4,
            Video = new VideoExportSettings
            {
                Codec = ExportCodec.H264,
                RateControl = RateControl.Vbr(4000, 6000),
                Profile = "high",
                Level = "4.1",
                Gop = new GopSettings { KeyframeSeconds = 1, BFrames = 2 },
            },
            Audio = new AudioExportSettings { Codec = ExportCodec.Aac },
        };
        ExportReport report = Export(ffmpeg, settings, TestEncoders.H264Nvenc, TestEncoders.AacMf, 90, out _);

        output.WriteLine(report.Video!.ToString());
        Assert.Equal("High", report.Video.Profile);
        Assert.True(report.Video.HasBFrames);
        Assert.Empty(ExportVerifier.Check(report, new ExportExpectation
        {
            FrameCount = 90, MaxGop = 30, Color = ExportColor.For(640, 360), FastStart = true, AudioCodec = "aac", AudioDurationSeconds = 3,
        }));
    }

    [ProxyFact]
    public void Two_pass_target_size_lands_near_the_target()
    {
        FfmpegExecutable ffmpeg = Skip.ProxyExecutable!;
        const long target = 600_000;
        var settings = new ExportSettings
        {
            Container = ExportContainer.WebM,
            Video = new VideoExportSettings { Codec = ExportCodec.Vp9, RateControl = RateControl.TargetSize(target) },
            Audio = new AudioExportSettings { Codec = ExportCodec.Opus, RateControl = new RateControl { Mode = RateControlMode.Vbr, BitrateKbps = 96 } },
        };

        ExportReport report = Export(ffmpeg, settings, Vp9, TestEncoders.Opus, 120, out int passes);

        output.WriteLine($"Objetivo {target / 1000} KB, salió {report.Bytes / 1000} KB en {passes} pasadas.");
        Assert.Equal(2, passes);
        // En 4 s libvpx se queda corto (≈ 85 %); nunca debe pasarse.
        Assert.InRange(report.Bytes, target * 0.75, target * 1.05);
    }

    [ProxyFact]
    public void Svt_av1_ten_bit_with_a_closed_one_second_gop()
    {
        FfmpegExecutable ffmpeg = Skip.ProxyExecutable!;
        var settings = new ExportSettings
        {
            Container = ExportContainer.Mkv,
            Video = new VideoExportSettings
            {
                Codec = ExportCodec.Av1, BitDepth = 10, RateControl = RateControl.ConstantQuality(40), SpeedPreset = "12",
                Gop = new GopSettings { KeyframeSeconds = 1 },
            },
        };

        ExportReport report = Export(ffmpeg, settings, TestEncoders.SvtAv1, null, 75, out _);

        Assert.Equal("yuv420p10le", report.Video!.PixelFormat);
        Assert.Equal(3, report.Video.KeyframeCount);
        Assert.Empty(ExportVerifier.Check(report, new ExportExpectation { FrameCount = 75, MaxGop = 30, Color = ExportColor.For(640, 360) }));
    }

    [ProxyFact]
    public void Prores_4444_keeps_the_alpha()
    {
        FfmpegExecutable ffmpeg = Skip.ProxyExecutable!;
        var settings = new ExportSettings
        {
            Container = ExportContainer.Mov,
            Video = new VideoExportSettings
            {
                Codec = ExportCodec.ProRes, RateControl = RateControl.ProfileDefined, Profile = "4444", BitDepth = 10,
                Chroma = ChromaSubsampling.Chroma444, Alpha = true,
            },
        };

        ExportReport report = Export(ffmpeg, settings, TestEncoders.ProRes, null, 10, out _);

        Assert.StartsWith("yuva444", report.Video!.PixelFormat, StringComparison.Ordinal);
        Assert.Equal("4444", report.Video.Profile);
    }

    private static EncoderSpec Vp9 { get; } = TestEncoders.SvtAv1 with
    {
        Id = "libvpx-vp9", Codec = ExportCodec.Vp9, PixelFormats = [ExportPixelFormat.Yuv420P, ExportPixelFormat.Yuva420P],
        RateControls = [RateControlMode.ConstantQuality, RateControlMode.Vbr, RateControlMode.Abr], SupportsTwoPass = true,
        BaseArguments = ["-deadline", "good", "-cpu-used", "8", "-row-mt", "1"],
    };

    private static string Head(IReadOnlyList<string> args, int count) => string.Join(' ', args.Take(count));

    // Exporta con el sink real ruido gris en movimiento a 640×360 y devuelve lo que dice ffprobe.
    private static ExportReport Export(FfmpegExecutable ffmpeg, ExportSettings settings, EncoderSpec video, EncoderSpec? audio, int frames,
        out int passes)
    {
        string directory = Path.Combine(Path.GetTempPath(), "rodaje-pruebas-export", "argumentos ñ", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            VideoExportSettings v = settings.Video!;
            ExportPixelFormat pixels = video.PixelFormatFor(v.BitDepth, v.Chroma, v.Alpha)!.Value;
            var format = new ExportVideoFormat(640, 360, Rate, pixels, ExportColor.For(640, 360, pixels));
            TimeSpan duration = TimeSpan.FromSeconds(frames / Rate.ToDouble());
            EncoderArguments args = FfmpegArguments.Build(settings, video, pixels, audio, Rate, duration);
            passes = args.PassCount;
            string? wav = null;
            if (audio is not null)
            {
                wav = Path.Combine(directory, "mezcla.rodaje-tmp.wav");
                using var writer = new WaveFileWriter(wav, ExportAudioFormat.Default);
                writer.Write(new float[(int)(duration.TotalSeconds * 48_000) * 2]);
                writer.Complete();
            }

            string outputPath = Path.Combine(directory, "salida." + settings.Container.Extension());
            var request = new ExportSinkRequest(outputPath, settings.Container, format, video, args.Video, audio is null ? null : ExportAudioFormat.Default,
                wav, audio, args.Audio, frames)
            {
                PassCount = args.PassCount,
                PassLogPath = ExportOutputGuard.TemporaryPath(outputPath) + ".pasadas",
            };
            byte[] frame = new byte[format.FrameBytes];
            using var sink = new FfmpegPipeSink(ffmpeg);
            for (int pass = 1; pass <= args.PassCount; pass++)
            {
                sink.Start(request with { Pass = pass }, CancellationToken.None);
                var random = new Random(1);
                for (int i = 0; i < frames; i++)
                {
                    Fill(frame, format, random, i);
                    sink.WriteVideo(Frame(frame, format, i), CancellationToken.None);
                }

                sink.Complete(CancellationToken.None);
            }

            return ExportVerifier.For(ffmpeg).Inspect(outputPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Degradado que se desplaza con algo de ruido: comprimible, pero con detalle que cuesta bits.
    private static void Fill(byte[] frame, ExportVideoFormat format, Random random, int index)
    {
        bool wide = format.PixelFormat.BytesPerSample() == 2;
        for (int i = 0; i < frame.Length; i++)
        {
            frame[i] = (byte)(((i % format.Width) / 3) + (index * 2) + random.Next(24));
            if (wide && (i & 1) == 1)
            {
                frame[i] &= 0x03;
            }
        }
    }

    private static ExportVideoFrame Frame(byte[] data, ExportVideoFormat format, long index)
    {
        ExportPixelFormat pixels = format.PixelFormat;
        int[] offsets = new int[5];
        int[] pitches = new int[4];
        for (int p = 0; p < pixels.PlaneCount(); p++)
        {
            pitches[p] = pixels.RowBytes(p, format.Width, format.Height);
            offsets[p + 1] = offsets[p] + (pitches[p] * pixels.PlaneSize(p, format.Width, format.Height).Height);
        }

        ReadOnlySpan<byte> Plane(int p) => p < pixels.PlaneCount() ? data.AsSpan(offsets[p], offsets[p + 1] - offsets[p]) : default;
        return new ExportVideoFrame(index, format, Plane(0), pitches[0], Plane(1), pitches[1], Plane(2), pitches[2], Plane(3), pitches[3]);
    }
}
