using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export.Running;
using Rodaje.Media.Export;

namespace Rodaje.Export.Tests.Running;

/// <summary>
/// La planificación en los extremos (tarea 229): tasas de 1 a 1000 fps, 24 h a 23,976/29,97/59,94, el último cuadro,
/// tamaños de 1×1 a 16384×16384 y más, vertical, entrelazado, 360 y cada codificador del catálogo. Sin FFmpeg ni GPU.
/// </summary>
public sealed class ExportPlanLimitsTests
{
    private static readonly IReadOnlyList<EncoderSpec> Encoders = ExportEncoderCatalog.All;

    private static ExportSettings Web { get; } = ExportPresets.YouTube1080p.Settings;

    [Fact]
    public void A_sequence_larger_than_a_texture_says_why_instead_of_throwing_out_of_range()
    {
        Project project = Sized(20_000, 20_000);

        ExportException error = Invalid(() => ExportPlan.Create(Request(project, Web with { Video = Web.Video! with { Width = null, Height = null } }), Encoders));

        Assert.Contains("20000×20000", error.Message, StringComparison.Ordinal);
        Assert.Contains("16384", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void One_side_asked_on_a_very_tall_sequence_says_why_instead_of_throwing_out_of_range()
    {
        // Vertical 9:16 con 16384 de ancho: el alto que sigue su forma pasa de 16384. El GIF de 480 sobre una tira de 2×16384, igual.
        Invalid(() => ExportPlan.Create(Request(Sized(1080, 1920), Web with { Video = Web.Video! with { Width = 16384, Height = null } }), Encoders));
        Invalid(() => ExportPlan.Create(Request(Sized(2, 16384), ExportPresets.Gif.Settings), Encoders));

        ExportPlan fits = ExportPlan.Create(Request(Sized(1080, 1920), Web with { Video = Web.Video! with { Width = null, Height = 16384 } }), Encoders);
        Assert.Equal((9216, 16384), (fits.Video!.Width, fits.Video.Height));
    }

    [Fact]
    public void Interlaced_checks_the_height_it_takes_from_the_sequence()
    {
        ExportSettings prores = ExportPresets.ProResMaster.Settings;
        ExportSettings interlaced = prores with { Video = prores.Video! with { FieldOrder = ExportFieldOrder.TopFirst, FrameRate = Rational.Ntsc30 } };

        // ProRes es 4:2:2 o 4:4:4: basta un alto par (tarea 232), y el de la secuencia ya se redondea a par.
        ExportPlan odd = ExportPlan.Create(Request(Sized(1920, 1081), interlaced), Encoders);
        Assert.Equal((1920, 1080), (odd.Video!.Width, odd.Video.Height));
        Assert.Equal(1082, ExportPlan.Create(Request(Sized(1920, 1082), interlaced), Encoders).Video!.Height);
        Assert.Equal(2, ExportPlan.Create(Request(Sized(2, 2), interlaced), Encoders).Video!.Height);

        ExportPlan plan = ExportPlan.Create(Request(Sized(1920, 1080), interlaced), Encoders);
        Assert.Equal((1920, 1080, new Rational(60000, 1001)), (plan.Video!.Width, plan.Video.Height, plan.OutputRate));
    }

    /// <summary>
    /// D1 NTSC (720×486) y DV NTSC (720×480) en ProRes entrelazado sin tamaño pedido (tarea 232): ProRes es 4:2:2 o
    /// 4:4:4, el croma tiene todas las filas y cada campo de 243 líneas lo lleva entero; antes se pedía un múltiplo de 4,
    /// la regla del 4:2:0, y el plan decía «Elige un tamaño de salida».
    /// </summary>
    [Theory]
    [InlineData(486, ExportFieldOrder.BottomFirst, "hq")]
    [InlineData(480, ExportFieldOrder.BottomFirst, "hq")]
    [InlineData(486, ExportFieldOrder.TopFirst, "4444")]
    public void Ntsc_sd_in_interlaced_prores_is_planned_at_the_sequence_size(int height, ExportFieldOrder order, string profile)
    {
        ExportSettings prores = ExportPresets.ProResMaster.Settings;
        ExportSettings interlaced = prores with
        {
            Video = prores.Video! with
            {
                Width = null, Height = null, FieldOrder = order, FrameRate = Rational.Ntsc30, Profile = profile,
                Chroma = profile == "hq" ? ChromaSubsampling.Chroma422 : ChromaSubsampling.Chroma444,
            },
        };

        ExportPlan plan = ExportPlan.Create(Request(Sized(720, height), interlaced), Encoders);

        Assert.Equal((720, height), (plan.Video!.Width, plan.Video.Height));
        Assert.True(plan.Interlaced);
        Assert.Equal(new Rational(60000, 1001), plan.OutputRate);
        Assert.Contains("separatefields", plan.VideoFilters);
    }

    /// <summary>Con croma 4:2:0 el campo lleva su croma a media altura: el alto sigue teniendo que ser múltiplo de 4.</summary>
    [Fact]
    public void Interlaced_420_still_needs_a_height_multiple_of_four()
    {
        Assert.Equal(4, ExportPixelFormat.Nv12.InterlacedHeightStep());
        Assert.Equal(4, ExportPixelFormat.Yuv420P10.InterlacedHeightStep());
        Assert.Equal(2, ExportPixelFormat.Yuv422P10.InterlacedHeightStep());
        Assert.Equal(2, ExportPixelFormat.Yuva444P10.InterlacedHeightStep());

        // Un ProRes de mentira que codificara 4:2:0: el validador mira el croma del formato, no el códec.
        EncoderSpec prores420 = Encoders.First(e => e.Codec == ExportCodec.ProRes) with
        {
            PixelFormats = [ExportPixelFormat.Yuv420P10],
            Profiles = [],
        };
        ExportSettings prores = ExportPresets.ProResMaster.Settings;
        VideoExportSettings video = prores.Video! with
        {
            Width = 720, Height = 486, FieldOrder = ExportFieldOrder.BottomFirst, FrameRate = Rational.Ntsc30,
            Profile = null, Chroma = ChromaSubsampling.Chroma420, Alpha = false,
        };

        IReadOnlyList<ExportProblem> problems = ExportSettingsValidator.Validate(prores with { Video = video, Audio = null }, prores420, null);
        Assert.Contains(problems, p => p.Message.Contains("4:2:0, el alto tiene que ser múltiplo de 4", StringComparison.Ordinal));

        problems = ExportSettingsValidator.Validate(prores with { Video = video with { Height = 480 }, Audio = null }, prores420, null);
        Assert.DoesNotContain(problems, p => p.Message.Contains("alto", StringComparison.Ordinal));
    }

    [Fact]
    public void A_360_export_checks_the_shape_it_takes_from_the_sequence()
    {
        ExportSettings spherical = Web with { Video = Web.Video! with { Width = null, Height = null, Projection = ExportProjection.Equirectangular } };

        ExportException error = Invalid(() => ExportPlan.Create(Request(Sized(1920, 1080), spherical), Encoders));
        Assert.Contains("2:1", error.Message, StringComparison.Ordinal);
        Invalid(() => ExportPlan.Create(Request(Sized(1920, 1080), spherical with { Video = spherical.Video! with { Height = 1080 } }), Encoders));

        ExportPlan plan = ExportPlan.Create(Request(Sized(3840, 1920), spherical with { Video = spherical.Video! with { Width = 1920 } }), Encoders);
        Assert.Equal((1920, 960), (plan.Video!.Width, plan.Video.Height));
    }

    [Fact]
    public void A_crop_whose_origin_overflows_an_int_is_out_of_the_sequence()
    {
        ExportSettings cropped = Web with { Video = Web.Video! with { Crop = new ExportCrop(int.MaxValue - 5, int.MaxValue - 5, 10, 10) } };

        Assert.Contains("se sale", Invalid(() => ExportPlan.Create(Request(Sized(1920, 1080), cropped), Encoders)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_output_rate_outside_one_to_a_thousand_fps_says_why_instead_of_overflowing()
    {
        foreach (Rational rate in new[] { new Rational(long.MaxValue, 1), new Rational(1001, 1), new Rational(1, 2) })
        {
            ExportSettings settings = Web with { Video = Web.Video! with { FrameRate = rate } };
            Assert.Contains("entre 1 y 1000", Invalid(() => ExportPlan.Create(Request(Sized(1920, 1080), settings), Encoders)).Message, StringComparison.Ordinal);
        }

        ExportPlan fastest = ExportPlan.Create(Request(Sized(1920, 1080), Web with { Video = Web.Video! with { FrameRate = new Rational(1000, 1) } }), Encoders);
        Assert.Equal(1000L * 60, fastest.FrameCount);
    }

    [Fact]
    public void Target_size_and_buffer_do_not_wrap_around_with_huge_values()
    {
        // 2^62 bytes en 10^9 s: 36 155 618 kbps. targetBytes * 8 en long se envolvía y daba 1 kbps.
        Assert.Equal(36_155_618, RateControl.BitrateForTargetSize(1L << 62, TimeSpan.FromSeconds(1e9), 0));
        Assert.Equal(int.MaxValue, RateControl.BitrateForTargetSize(long.MaxValue, TimeSpan.FromSeconds(1), 0));

        EncoderSpec nvenc = ExportEncoderCatalog.Find("h264_nvenc")!;
        IReadOnlyList<string> args = FfmpegArguments.Video(new VideoExportSettings { Codec = ExportCodec.H264, RateControl = RateControl.Vbr(1_000, int.MaxValue) },
            nvenc, ExportPixelFormat.Nv12, Rational.Fps30, TimeSpan.FromSeconds(1), 0);
        Assert.Equal("4294967294k", args[args.ToList().IndexOf("-bufsize") + 1]);
        IReadOnlyList<string> cbr = FfmpegArguments.Video(new VideoExportSettings { Codec = ExportCodec.H264, RateControl = RateControl.Cbr(int.MaxValue) },
            nvenc, ExportPixelFormat.Nv12, Rational.Fps30, TimeSpan.FromSeconds(1), 0);
        Assert.Equal("4294967294k", cbr[cbr.ToList().IndexOf("-bufsize") + 1]);
    }

    [Fact]
    public void The_disk_estimate_of_a_target_size_over_one_frame_stays_positive()
    {
        Project project = Sized(1920, 1080);
        project.Sequences[0].FrameRate = new Rational(1000, 1);
        ExportSettings settings = new()
        {
            Container = ExportContainer.WebM,
            Video = new VideoExportSettings { Codec = ExportCodec.Av1, EncoderId = "libaom-av1", RateControl = RateControl.TargetSize(4_000_000_000) },
            Audio = new AudioExportSettings { Codec = ExportCodec.Opus, RateControl = new RateControl { Mode = RateControlMode.Vbr, BitrateKbps = 96 } },
        };
        Time last = Time.FromFrames(59_999, project.Sequences[0].FrameRate);

        ExportPlan plan = ExportPlan.Create(Request(project, settings, new ExportRange(last, project.Sequences[0].Duration)), Encoders);

        Assert.Equal(1, plan.FrameCount);
        Assert.InRange(ExportJob.EstimateBytes(plan)!.Value, 4_000_000_000, 4_000_000_100);
    }

    /// <summary>
    /// Cada preajuste y cada codificador del catálogo, por cada tamaño, tasa, duración y rango extremo: o se planifica
    /// cumpliendo los invariantes, o falla con <see cref="ExportErrorCode.InvalidSettings"/> y su motivo; nunca otra excepción.
    /// </summary>
    [Fact]
    public void Every_preset_and_encoder_plans_at_the_extremes_or_says_why()
    {
        (int, int)[] sizes = [(1, 1), (2, 2), (7, 5), (8193, 4321), (16384, 16384), (1080, 1920), (2, 16384), (16384, 2), (20000, 20000), (1920, 1082)];
        Rational[] rates = [new(1, 1), new(1000, 1), Rational.Ntsc24, Rational.Ntsc30, Rational.Ntsc60];
        var failures = new List<string>();
        int planned = 0;
        foreach (ExportSettings settings in AllSettings())
        {
            foreach ((int w, int h) in sizes)
            {
                foreach (Rational rate in rates)
                {
                    foreach (Time duration in new[] { Time.FromSeconds(86_400), Time.FromFrames(1, rate), Sequence.MaxTime })
                    {
                        Time lastFrame = Time.FromFrames(Math.Max(0, duration.ToFrames(rate) - 1), rate);
                        foreach (ExportRange? range in new ExportRange?[] { null, new ExportRange(lastFrame, duration) })
                        {
                            Project project = Sized(w, h, duration);
                            project.Sequences[0].FrameRate = rate;
                            string label = string.Create(CultureInfo.InvariantCulture,
                                $"{settings.Container}/{settings.Video?.EncoderId ?? settings.Video?.Codec.ToString()} {w}×{h} a {rate}, {duration.Ticks} ticks, {(range is null ? "todo" : "último cuadro")}");
                            try
                            {
                                ExportPlan plan = ExportPlan.Create(Request(project, settings, range), Encoders);
                                planned++;
                                if (Broken(plan) is { } why)
                                {
                                    failures.Add($"{label}: {why}");
                                }
                            }
                            catch (ExportException ex) when (ex.Code == ExportErrorCode.InvalidSettings && ex.Message.Length > 0)
                            {
                            }
#pragma warning disable CA1031 // Cualquier otra excepción es el defecto que se busca.
                            catch (Exception ex)
#pragma warning restore CA1031
                            {
                                failures.Add($"{label}: {ex.GetType().Name}: {ex.Message}");
                            }
                        }
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
        Assert.True(planned > 10_000, $"Solo se planificaron {planned}.");
    }

    [Fact]
    public void The_arguments_do_not_depend_on_the_culture_of_the_machine()
    {
        Project project = Sized(1920, 1080);
        project.Sequences[0].FrameRate = Rational.Ntsc30;
        foreach (ExportSettings settings in AllSettings().Where(s => s.Video is not { Projection: ExportProjection.Equirectangular, Width: null }))
        {
            ExportPlan invariant = ExportPlan.Create(Request(project, settings), Encoders);
            CultureInfo saved = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            try
            {
                ExportPlan german = ExportPlan.Create(Request(project, settings), Encoders);
                Assert.Equal(invariant.Arguments.Video, german.Arguments.Video);
                Assert.Equal(invariant.Arguments.Audio, german.Arguments.Audio);
                Assert.Equal(invariant.VideoFilters, german.VideoFilters);
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }
    }

    private static string? Broken(ExportPlan plan)
    {
        if (plan.FrameCount <= 0 || plan.FirstFrame < 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"cuadros {plan.FirstFrame}+{plan.FrameCount}");
        }

        // La duración es la de los cuadros enteros: ni se redondea de más ni desborda.
        double seconds = (plan.End - plan.Start).ToSeconds();
        double frames = plan.FrameCount / plan.OutputRate.ToDouble();
        if (Math.Abs(seconds - frames) > 1e-6 * Math.Max(1, frames))
        {
            return string.Create(CultureInfo.InvariantCulture, $"dura {seconds} s y sus cuadros {frames} s");
        }

        ExportExpectation? expected = plan.Expectation();
        if (plan.Video is { } video)
        {
            if (video.Width > ExportVideoFormat.MaxDimension || video.Height > ExportVideoFormat.MaxDimension
                || (video.PixelFormat.NeedsEvenWidth() && video.Width % 2 != 0) || (video.PixelFormat.HalfHeightChroma() && video.Height % 2 != 0))
            {
                return string.Create(CultureInfo.InvariantCulture, $"tamaño {video.Width}×{video.Height} en {video.PixelFormat}");
            }

            if (plan.Interlaced && (video.Height % video.PixelFormat.InterlacedHeightStep() != 0 || plan.FrameCount % 2 != 0))
            {
                return string.Create(CultureInfo.InvariantCulture, $"entrelazado de {video.Height} filas y {plan.FrameCount} campos");
            }

            if (plan.Settings.Video!.Projection == ExportProjection.Equirectangular && video.Width != 2 * video.Height)
            {
                return string.Create(CultureInfo.InvariantCulture, $"360 de {video.Width}×{video.Height}");
            }

            if (expected is not null && expected.FrameCount != plan.EncodedFrameCount)
            {
                return "la verificación espera otra cuenta de cuadros";
            }
        }

        return plan.Arguments.Video.Concat(plan.Arguments.Audio).FirstOrDefault(a => a.Contains("NaN", StringComparison.Ordinal)
            || a.Contains('∞') || (a.StartsWith('-') && char.IsDigit(a.ElementAtOrDefault(1))));
    }

    private static IEnumerable<ExportSettings> AllSettings()
    {
        foreach (ExportPreset preset in ExportPresets.All)
        {
            yield return preset.Settings;
        }

        ExportContainer[] containers = [ExportContainer.Mp4, ExportContainer.Mov, ExportContainer.Mkv, ExportContainer.WebM, ExportContainer.Gif,
            ExportContainer.WebP, ExportContainer.ImageSequence];
        foreach (EncoderSpec encoder in ExportEncoderCatalog.Video)
        {
            ExportPixelFormat pixels = encoder.PixelFormats[0];
            RateControlMode mode = encoder.RateControls[0];
            yield return new ExportSettings
            {
                Container = containers.First(c => c.Allows(encoder.Codec)),
                Video = new VideoExportSettings
                {
                    Codec = encoder.Codec, EncoderId = encoder.Id, BitDepth = pixels.BitDepth(), Chroma = pixels.Chroma(), Alpha = pixels.HasAlpha(),
                    RateControl = mode is RateControlMode.Vbr or RateControlMode.Cbr or RateControlMode.Abr
                        ? new RateControl { Mode = mode, BitrateKbps = 5_000, MaxBitrateKbps = 5_000 }
                        : new RateControl { Mode = mode },
                },
            };
        }

        ExportSettings prores = ExportPresets.ProResMaster.Settings;
        foreach (ExportFieldOrder order in new[] { ExportFieldOrder.TopFirst, ExportFieldOrder.BottomFirst })
        {
            yield return prores with { Video = prores.Video! with { FieldOrder = order, FrameRate = Rational.Ntsc30 } };
            yield return prores with { Video = prores.Video! with { FieldOrder = order, FrameRate = Rational.Fps25, Width = 1920, Height = 1080 } };
        }

        yield return Web with { Video = Web.Video! with { Projection = ExportProjection.Equirectangular, Width = 3840, Height = 1920 } };
        yield return Web with { Video = Web.Video! with { Projection = ExportProjection.Equirectangular, Width = null, Height = null } };
        yield return Web with { Video = Web.Video! with { Width = 16384, Height = null } };
        yield return Web with { Video = Web.Video! with { Width = null, Height = 2 } };
        yield return Web with { Video = Web.Video! with { RateControl = RateControl.TargetSize(4_000_000_000) }, Audio = null };
        yield return Web with { Video = Web.Video! with { FrameRate = new Rational(1000, 1) } };
        yield return Web with { Video = Web.Video! with { FrameRate = Rational.Ntsc60 } };
        yield return Web with { Audio = null };
    }

    private static Project Sized(int width, int height, Time? duration = null)
    {
        Project project = ExportPlanTests.Project(duration ?? Time.FromSeconds(60));
        project.Sequences[0].Width = width;
        project.Sequences[0].Height = height;
        return project;
    }

    private static ExportRequest Request(Project project, ExportSettings settings, ExportRange? range = null) =>
        ExportPlanTests.Request(project, settings, range);

    private static ExportException Invalid(Action action)
    {
        ExportException error = Assert.Throws<ExportException>(action);
        Assert.Equal(ExportErrorCode.InvalidSettings, error.Code);
        return error;
    }
}
