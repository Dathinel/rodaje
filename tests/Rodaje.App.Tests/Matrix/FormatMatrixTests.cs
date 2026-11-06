using System.Diagnostics;
using System.Globalization;
using Rodaje.App.Services;
using Rodaje.App.Services.Media;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;
using Rodaje.Export.Encoders;
using Rodaje.Export.Running;
using Rodaje.Media.Audio;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;
using Rodaje.Media.Video;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.Matrix;

/// <summary>
/// La matriz de formatos de punta a punta (<c>Categoria=Matriz</c>, solo con <c>RODAJE_PRUEBAS_LARGAS=1</c>): cada archivo
/// de <see cref="MatrixCases"/> se importa, da miniaturas y forma de onda, se decodifica en orden y con seek exacto al
/// cuadro (el número dibujado), en reversa, y un proyecto con un corte, un tramo a 2× y otro en reversa se exporta a MP4
/// H.264 y a MKV FFV1 + FLAC; ffprobe comprueba cuadros, duración, audio y color, y en la copia sin pérdida se lee el
/// número de cada cuadro. Lo que Rodaje no admite debe dar un motivo claro, nunca una excepción.
/// La tabla queda en <c>TestResults\matriz\tabla.md</c>.
/// </summary>
[Collection(MatrixGroup.Name)]
[Trait("Categoria", "Matriz")]
public sealed class FormatMatrixTests(ITestOutputHelper output)
{
    // Corte de 24 cuadros desde el 6; 30 cuadros desde el 30 a 2× (15); 30 cuadros desde el 10 en reversa.
    private const int CutIn = 6, CutLength = 24, FastIn = 30, FastLength = 30, ReverseIn = 10, ReverseLength = 30;
    private const int OutputFrames = CutLength + (FastLength / 2) + ReverseLength;

    private static readonly Lazy<(GpuDevice? Device, string? Failure)> Gpu = new(() =>
    {
        GpuDevice? device = GpuDevice.TryCreate(out string? failure);
        return (device, failure ?? (device is { SupportsHardwareDecoding: false } ? "la GPU no ofrece D3D11VA" : null));
    });

    private static readonly Lazy<Task<IReadOnlyList<EncoderSpec>>> Encoders = new(() =>
        new CatalogEncoderSource(Skip.ProxyExecutable!).GetAsync());

    public static TheoryData<string> Cases => MatrixCases.Names();

    /// <summary>Número de cuadro de origen que debe verse en el cuadro <paramref name="k"/> exportado.</summary>
    internal static int ExpectedSourceFrame(int k) => k < CutLength
        ? CutIn + k
        : k < CutLength + (FastLength / 2) ? FastIn + (2 * (k - CutLength)) : ReverseIn + ReverseLength - 1 - (k - CutLength - (FastLength / 2));

    [Fact]
    public void The_expected_frames_follow_the_cut_the_speed_and_the_reverse()
    {
        Assert.Equal([6, 29, 30, 32, 58, 39, 10], new[] { 0, 23, 24, 25, 38, 39, 68 }.Select(ExpectedSourceFrame));
    }

    [MatrixTheory]
    [MemberData(nameof(Cases))]
    public async Task Each_format_goes_end_to_end(string name)
    {
        MatrixCase c = MatrixCases.Get(name);
        var row = new MatrixRow(c);
        try
        {
            await RunAsync(c, row);
        }
        catch (Exception ex)
        {
            row.Fail("excepción", $"{ex.GetType().Name}: {ex.Message}");
        }

        MatrixTable.Save(row);
        output.WriteLine(row.ToMarkdown());
        foreach (string note in row.Notes)
        {
            output.WriteLine(note);
        }

        Assert.True(row.Failures.Count == 0, string.Join(Environment.NewLine, row.Failures));
    }

    private static async Task RunAsync(MatrixCase c, MatrixRow row)
    {
        FfmpegTool ffmpeg = FfmpegTool.Default!;
        var watch = Stopwatch.StartNew();
        (string? path, string? problem) = MatrixMedia.GetOrCreate(ffmpeg, c);
        if (path is null)
        {
            row.NotGenerated(problem!);
            return;
        }

        row.Note($"Archivo: {path} ({watch.ElapsedMilliseconds} ms)");

        // Importar: lo mismo que EditorSession.ImportMediaAsync.
        watch.Restart();
        MediaProbeResult probe = new FfmpegMediaProber().Probe(path);
        if (c.ExpectedProblem is { } expected)
        {
            if (!probe.IsUsable && probe.Problem!.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                row.Set(MatrixStep.Import, $"rechazado: «{probe.Problem}»");
                row.Unsupported = true;
            }
            else
            {
                row.Fail(MatrixStep.Import, probe.IsUsable ? "se importó y debía rechazarse" : $"motivo inesperado: {probe.Problem}");
            }

            return;
        }

        if (!probe.IsUsable)
        {
            row.Fail(MatrixStep.Import, probe.Problem ?? "sin motivo");
            return;
        }

        MediaInfo info = probe.Info!;
        var media = new MediaRef { Id = "m", Path = path };
        info.ApplyTo(media);
        row.Set(MatrixStep.Import, Invariant($"{Describe(info)}; {watch.ElapsedMilliseconds} ms"));
        CheckImport(c, info, row);

        if (c.HasVideo)
        {
            Thumbnails(c, path, info, row);
            if (info.IsStillImage)
            {
                using var decoder = VideoDecoder.Open(path);
                using var first = decoder.GetFrame(0);
                Assert.NotNull(first);
                for (int n = 0; n < 60; n++)
                {
                    using var repeated = decoder.GetFrame(n);
                    Assert.Same(first, repeated);
                }

                row.Set(MatrixStep.Play, "cuadro 0 reutilizado 60 veces");
                row.Set(MatrixStep.Seek, "cuadro 0 en cualquier instante");
                row.Set(MatrixStep.Reverse, "imagen fija");
            }
            else
            {
                int frames = (int)info.Video!.FrameCount;
                DecodeAndSeek(c, path, frames, row);
                Reverse(c, path, frames, row);
            }
        }

        if (c.HasAudio)
        {
            Waveform(c, path, row);
        }

        await ExportAsync(info.IsStillImage ? c with { FrameCodes = false } : c, media, info, row);
    }

    private static void CheckImport(MatrixCase c, MediaInfo info, MatrixRow row)
    {
        if (c.HasVideo)
        {
            VideoStreamInfo v = info.Video!;
            (int w, int h) = c.Rotation is 90 or 270 ? (c.Height, c.Width) : (c.Width, c.Height);
            if ((v.DisplayWidth, v.DisplayHeight) != (w, h))
            {
                row.Fail(MatrixStep.Import, Invariant($"tamaño en pantalla {v.DisplayWidth}×{v.DisplayHeight}, se esperaba {w}×{h}"));
            }

            string args = string.Join(' ', c.Video);
            string? hdr = args.Contains("smpte2084", StringComparison.Ordinal) ? "PQ" : args.Contains("arib-std-b67", StringComparison.Ordinal) ? "HLG" : null;
            if (c.FrameCodes && v.HdrTransfer != hdr)
            {
                row.Fail(MatrixStep.Import, $"HDR «{v.HdrTransfer}», se esperaba «{hdr}»");
            }

            if (!info.IsStillImage && !c.VariableRate && v.FrameRate != c.Rate)
            {
                row.Fail(MatrixStep.Import, Invariant($"tasa {v.FrameRate}, se esperaba {c.Rate}"));
            }
        }

        if (c.HasAudio != info.HasAudio)
        {
            row.Fail(MatrixStep.Import, c.HasAudio ? "no encontró el audio" : "encontró un audio que no hay");
        }

        if (!info.IsStillImage && Math.Abs(info.Duration.ToSeconds() - c.Seconds) > 0.1)
        {
            row.Fail(MatrixStep.Import, Invariant($"duración {info.Duration.ToSeconds():0.000} s, se esperaba {c.Seconds:0.###}"));
        }
    }

    internal static void Thumbnails(MatrixCase c, string path, MediaInfo info, MatrixRow row)
    {
        var watch = Stopwatch.StartNew();
        using ThumbnailDecoder decoder = ThumbnailDecoder.Open(path);
        int count = 0;
        foreach (double at in new[] { 0, c.Seconds / 2, c.Seconds - 0.2 })
        {
            BgraImage? image = decoder.Decode(Time.FromSeconds(at), 90);
            if (image is null)
            {
                row.Fail(MatrixStep.Thumbnails, Invariant($"sin miniatura en {at:0.00} s"));
                continue;
            }

            int expectedWidth = ThumbnailSize.WidthFor(info.Video!.DisplayWidth, info.Video.DisplayHeight, 90);
            if (image.Height != 90 || image.Width != expectedWidth)
            {
                row.Fail(MatrixStep.Thumbnails, Invariant($"miniatura de {image.Width}×{image.Height}, se esperaba {expectedWidth}×90"));
            }

            count++;
        }

        row.Set(MatrixStep.Thumbnails, Invariant($"{count}/3; {watch.ElapsedMilliseconds} ms"));
    }

    private static void DecodeAndSeek(MatrixCase c, string path, int frames, MatrixRow row)
    {
        var options = Gpu.Value.Device is { SupportsHardwareDecoding: true } gpu
            ? new VideoDecoderOptions { Device = gpu }
            : new VideoDecoderOptions();
        if (Gpu.Value.Failure is { } failure)
        {
            row.Note("Sin GPU: " + failure);
        }

        var watch = Stopwatch.StartNew();
        using VideoDecoder decoder = VideoDecoder.Open(path, options);

        // En un MKV el número de cuadros sale de la duración del contenedor, que el audio puede alargar: los índices de
        // más muestran el último cuadro, como un clip de teléfono cuyo audio dura más.
        int real = (int)Math.Round(c.Seconds * c.Rate.Num / c.Rate.Den);
        bool Matches(int n, int? code) => code == n || (n >= real && code == real - 1);

        // Reproducir: en orden, cuadro a cuadro.
        int wrong = 0, last = -1, unreadable = 0;
        for (int i = 0; i < frames; i++)
        {
            using Rodaje.Playback.DecodedFrame? frame = decoder.GetFrame(i);
            int? code = frame is null ? null : ReadCode(frame);
            if (code is null)
            {
                unreadable++;
            }
            else if (c.VariableRate ? code < last : !Matches(i, code))
            {
                wrong++;
            }

            last = code ?? last;
        }

        double playMs = watch.Elapsed.TotalMilliseconds;
        // get_format elige D3D11VA o software al decodificar el primer paquete, no al abrir.
        string engine = decoder.UsesHardwareDecoding ? "GPU" : "CPU";
        if (wrong > 0 || unreadable > (c.VariableRate ? frames / 10 : 0))
        {
            row.Fail(MatrixStep.Play, $"{wrong} cuadros distintos y {unreadable} ilegibles de {frames} ({engine})");
        }

        // Seek: primero, último, mitad y al azar, en desorden.
        var rng = new Random(20261012);
        int[] requests = [frames - 1, 0, frames / 2, 1, frames - 2, .. Enumerable.Range(0, 12).Select(_ => rng.Next(frames))];
        int misses = 0;
        watch.Restart();
        foreach (int n in requests)
        {
            using Rodaje.Playback.DecodedFrame? frame = decoder.GetFrame(n);
            int? code = frame is null ? null : ReadCode(frame);
            if (c.VariableRate ? code is null || Math.Abs(code.Value - n) > 1 : !Matches(n, code))
            {
                misses++;
                row.Note(Invariant($"seek {n}: se vio {code?.ToString(CultureInfo.InvariantCulture) ?? "nada"}"));
            }
        }

        if (misses > 0)
        {
            row.Fail(MatrixStep.Seek, $"{misses}/{requests.Length} seeks no dieron el cuadro pedido ({engine})");
        }

        row.Set(MatrixStep.Play, Invariant($"{frames - wrong - unreadable}/{frames} {engine}; {frames * 1000 / Math.Max(1, playMs):0} fps"));
        row.Set(MatrixStep.Seek, Invariant($"{requests.Length - misses}/{requests.Length}; {watch.Elapsed.TotalMilliseconds / requests.Length:0} ms/seek"));
    }

    private static void Reverse(MatrixCase c, string path, int frames, MatrixRow row)
    {
        var results = new List<string>();
        foreach (bool hardware in Gpu.Value.Device is { SupportsHardwareDecoding: true } ? new[] { false, true } : [false])
        {
            var watch = Stopwatch.StartNew();
            using VideoDecoder decoder = VideoDecoder.Open(path, hardware
                ? new VideoDecoderOptions { Device = Gpu.Value.Device }
                : new VideoDecoderOptions { AllowHardwareDecoding = false });
            KeyframeIndex keys = KeyframeIndex.Read(path);
            long first = Math.Max(0, frames - 40), lastFrame = frames - 1;
            long real = (long)Math.Round(c.Seconds * c.Rate.Num / c.Rate.Den);
            // Como la app: con D3D11VA, lo retenido tiene que caber en el pool del decodificador (ver OfflineReaders).
            using var reader = new ReverseFrameReader(decoder, keys, first, lastFrame, new ReverseFrameReaderOptions { MaxRetained = hardware ? 8 : 64 });
            long expected = lastFrame;
            int bad = 0;
            while (reader.Next() is { } frame)
            {
                using (frame)
                {
                    int? code = ReadCode(frame);
                    if (c.VariableRate ? code is null : code != Math.Min(expected, real - 1))
                    {
                        bad++;
                    }
                }

                expected--;
            }

            string engine = decoder.UsesHardwareDecoding ? "GPU" : "CPU";
            long delivered = lastFrame - expected;
            // En VFR cada índice muestra el cuadro más cercano a su instante: alguno se repite o se salta.
            if (bad > 0 || Math.Abs(delivered - (lastFrame - first + 1)) > (c.VariableRate ? 2 : 0))
            {
                row.Fail(MatrixStep.Reverse, $"{engine}: {delivered} entregados, {bad} distintos");
            }

            results.Add(Invariant($"{delivered - bad}/{lastFrame - first + 1} {engine} {watch.ElapsedMilliseconds} ms"));
        }

        row.Set(MatrixStep.Reverse, string.Join(", ", results));
    }
    internal static void Waveform(MatrixCase c, string path, MatrixRow row)
    {
        var watch = Stopwatch.StartNew();
        using AudioDecoder decoder = AudioDecoder.Open(path);
        var peaks = new WaveformPeaks(AudioDecoder.OutputSampleRate);
        var buffer = new float[4096 * AudioDecoder.OutputChannels];
        int read;
        while ((read = decoder.Read(buffer)) > 0)
        {
            peaks.Append(buffer.AsSpan(0, read * AudioDecoder.OutputChannels), AudioDecoder.OutputChannels);
        }

        peaks.Complete();
        double seconds = peaks.Samples / (double)AudioDecoder.OutputSampleRate;
        (float[] min, float[] max) = peaks.Query(0, peaks.Samples, 50);
        float top = max.Max();
        // Los pitidos duran 0,1 s cada 0,5 s: en 50 tramos de 50 ms hay picos y silencios.
        int loud = max.Count(m => m > 0.1f);
        if (Math.Abs(seconds - c.Seconds) > 0.1 || top < 0.2f || loud == 0 || loud == max.Length || decoder.DecodeErrors > 0)
        {
            row.Fail(MatrixStep.Waveform, Invariant($"{seconds:0.000} s, pico {top:0.00}, {loud}/50 tramos con sonido, {decoder.DecodeErrors} errores"));
        }

        row.Set(MatrixStep.Waveform, Invariant($"{seconds:0.00} s, pico {top:0.00}; {watch.ElapsedMilliseconds} ms"));
    }

    internal static async Task ExportAsync(MatrixCase c, MediaRef media, MediaInfo info, MatrixRow row)
    {
        Project project = BuildProject(c, media, info);
        Sequence sequence = project.Sequences[0];
        long expectedFrames = (sequence.Duration - Time.FromTicks(1)).ToFrames(sequence.FrameRate) + 1;

        var h264 = new ExportSettings
        {
            Video = new VideoExportSettings { Codec = ExportCodec.H264, EncoderId = "h264_nvenc", RateControl = RateControl.ConstantQuality(18) },
            Audio = new AudioExportSettings { Codec = ExportCodec.Aac },
        };
        var lossless = new ExportSettings
        {
            Container = ExportContainer.Mkv,
            Video = new VideoExportSettings { Codec = ExportCodec.Ffv1, RateControl = RateControl.Lossless },
            Audio = new AudioExportSettings { Codec = ExportCodec.Flac, RateControl = RateControl.Lossless },
            FastStart = false,
        };

        foreach ((MatrixStep step, ExportSettings settings, string file) in new[] { (MatrixStep.ExportH264, h264, "h264.mp4"), (MatrixStep.ExportLossless, lossless, "sin-perdida.mkv") })
        {
            string folder = Path.Combine(Path.GetTempPath(), "rodaje-matriz ñ", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, file);
                var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = Skip.ProxyExecutable!, TemporaryDirectory = folder });
                using ExportJob job = runner.Start(new ExportRequest(project, sequence.Id, path, settings), await Encoders.Value);
                await job.Completion.WaitAsync(TimeSpan.FromMinutes(5));
                if (job.Progress.State != ExportJobState.Completed)
                {
                    row.Fail(step, $"{job.Progress.State}: {job.Failure?.Message}");
                    continue;
                }

                ExportReport report = job.Report!;
                var problems = new List<string>();
                if (report.Video is not { } v)
                {
                    problems.Add("sin video");
                }
                else
                {
                    if (v.FrameCount != expectedFrames)
                    {
                        problems.Add(Invariant($"{v.FrameCount} cuadros, se esperaban {expectedFrames}"));
                    }

                    if ((v.Width, v.Height) != (sequence.Width, sequence.Height))
                    {
                        problems.Add(Invariant($"{v.Width}×{v.Height}"));
                    }

                    if (v.ColorSpace != "bt709" || v.ColorTransfer != "bt709" || v.ColorPrimaries != "bt709")
                    {
                        problems.Add($"color {v.ColorSpace}/{v.ColorTransfer}/{v.ColorPrimaries}");
                    }
                }

                double seconds = sequence.Duration.ToSeconds();
                if (c.HasAudio)
                {
                    if (report.Audio is not { } a)
                    {
                        problems.Add("sin audio");
                    }
                    else if (Math.Abs(a.DurationSeconds - seconds) > 0.05 || a.Channels != 2 || a.SampleRate != 48_000)
                    {
                        problems.Add(Invariant($"audio {a.Codec} {a.Channels} canales {a.SampleRate} Hz {a.DurationSeconds:0.000} s (secuencia {seconds:0.000} s)"));
                    }
                }

                string frameCheck = string.Empty;
                if (c.HasVideo && c.FrameCodes && report.Video is not null)
                {
                    (int good, int checkedFrames, string misses) = CheckExportedFrames(c, path, sequence);
                    // Sin pérdida, todos; en H.264 la compresión puede borrar algún bit del código.
                    int needed = step == MatrixStep.ExportLossless ? checkedFrames : checkedFrames * 9 / 10;
                    frameCheck = Invariant($", {good}/{checkedFrames} cuadros con su número");
                    if (good < needed)
                    {
                        problems.Add(Invariant($"solo {good} de {checkedFrames} cuadros muestran el cuadro de origen esperado ({misses})"));
                    }
                }

                string summary = Invariant($"{report.Video?.Codec} {report.Video?.PixelFormat} {report.Video?.FrameCount} cuadros{(report.Audio is { } au ? $" + {au.Codec}" : string.Empty)}{frameCheck}; {job.Progress.Elapsed.TotalSeconds:0.0} s");
                if (problems.Count > 0)
                {
                    row.Fail(step, string.Join("; ", problems) + " — " + summary);
                }
                else
                {
                    row.Set(step, summary);
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>Corte, tramo a 2× y tramo en reversa en video y audio (o solo audio), con la secuencia del primer video como al importar.</summary>
    internal static Project BuildProject(MatrixCase c, MediaRef media, MediaInfo info)
    {
        Rational rate = info.Video?.FrameRate ?? Rational.Fps30;
        var sequence = new Sequence
        {
            Id = "sec", Name = "Matriz", FrameRate = rate,
            Width = info.Video?.DisplayWidth ?? 1280, Height = info.Video?.DisplayHeight ?? 720,
        };
        var project = new Project { Id = "p", Name = c.Name, Sequences = [sequence], Media = [media] };
        var video = new Track { Id = "v0", Kind = TrackKind.Video, Name = "Video 1" };
        var audio = new Track { Id = "a0", Kind = TrackKind.Audio, Name = "Audio 1" };
        sequence.VideoTracks.Add(video);
        sequence.AudioTracks.Add(audio);

        Time F(long frames) => Time.FromFrames(frames, rate);
        (string Id, long In, long Length, ClipSpeed? Speed)[] parts =
        [
            ("corte", CutIn, CutLength, null),
            ("rapido", FastIn, FastLength, new ClipSpeed { Rate = new Rational(2, 1) }),
            ("reversa", ReverseIn, ReverseLength, new ClipSpeed { Rate = new Rational(1, 1), Reverse = true }),
        ];
        Time at = Time.Zero;
        foreach ((string id, long from, long length, ClipSpeed? speed) in parts)
        {
            Time end = at;
            foreach (Track track in new[] { video, audio })
            {
                if ((track == video && !c.HasVideo) || (track == audio && !c.HasAudio))
                {
                    continue;
                }

                var clip = new Clip { Id = $"{id}-{track.Id}", MediaId = media.Id, Start = at, SourceIn = F(from), SourceOut = F(from + length), LinkId = id, Speed = speed };
                if (speed is not null)
                {
                    ClipTimeMap.Fit(clip.Start, clip.SourceIn, clip.SourceOut, speed).ApplyTo(clip);
                }

                track.Clips.Add(clip);
                end = clip.End;
            }

            at = end;
        }

        var issues = ModelValidator.Validate(project);
        Assert.True(issues.Count == 0, string.Join(Environment.NewLine, issues));
        return project;
    }

    /// <summary>Lee el número dibujado en cada cuadro exportado (girándolo si el medio llevaba rotación).</summary>
    private static (int Good, int Checked, string Misses) CheckExportedFrames(MatrixCase c, string path, Sequence sequence)
    {
        int width = sequence.Width, height = sequence.Height;
        int good = 0, count = 0;
        var misses = new List<string>();
        FfmpegTool.Default!.DecodeGrayFrames(path, width, height, (index, gray) =>
        {
            if (index >= OutputFrames)
            {
                return;
            }

            count++;
            int expected = ExpectedSourceFrame(index);
            int? code = c.Rotation == 0 ? FrameCode.Read(gray, width, height, width) : ReadRotated(gray, width, height);
            if (c.VariableRate ? code is { } v && Math.Abs(v - expected) <= 1 : code == expected)
            {
                good++;
            }
            else if (misses.Count < 8)
            {
                misses.Add(Invariant($"{index}: {code?.ToString(CultureInfo.InvariantCulture) ?? "?"} en vez de {expected}"));
            }
        });
        return (good, count, string.Join(", ", misses));
    }

    private static int? ReadRotated(ReadOnlySpan<byte> gray, int width, int height)
    {
        // El cuadro exportado está girado: se prueban los dos sentidos.
        var turned = new byte[gray.Length];
        foreach (bool clockwise in new[] { true, false })
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Imagen nueva de height × width.
                    int nx = clockwise ? height - 1 - y : y;
                    int ny = clockwise ? x : width - 1 - x;
                    turned[(ny * height) + nx] = gray[(y * width) + x];
                }
            }

            if (FrameCode.Read(turned, height, width, height) is { } code)
            {
                return code;
            }
        }

        return null;
    }

    private static int? ReadCode(Rodaje.Playback.DecodedFrame frame)
    {
        LumaImage luma = FrameReadback.ReadLuma(frame);
        return FrameCode.Read(luma.Pixels, luma.Width, luma.Height, luma.Width);
    }

    internal static string Describe(MediaInfo info)
    {
        string video = info.Video is { } v
            ? Invariant($"{v.Codec} {v.PixelFormat} {v.DisplayWidth}×{v.DisplayHeight} {v.FrameRate}{(v.IsVariableFrameRate ? " VFR" : string.Empty)}{(v.Rotation != 0 ? $" rot {v.Rotation}" : string.Empty)}")
            : "sin video";
        string audio = info.Audio is { } a ? Invariant($"{a.Codec} {a.Channels} c {a.SampleRate} Hz") : "sin audio";
        return $"{video}, {audio}";
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MatrixGroup
{
    public const string Name = "Matriz de formatos";
}

/// <summary>Necesita <c>RODAJE_PRUEBAS_LARGAS=1</c>, las bibliotecas FFmpeg, el <c>ffmpeg.exe</c> y NVENC.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class MatrixTheoryAttribute : TheoryAttribute
{
    public MatrixTheoryAttribute() => Skip = Rodaje.TestMedia.Skip.First(Rodaje.TestMedia.Skip.LongRun, Rodaje.TestMedia.Skip.FfmpegLibraries,
        Rodaje.TestMedia.Skip.Proxies, Rodaje.TestMedia.Skip.Nvenc);
}
