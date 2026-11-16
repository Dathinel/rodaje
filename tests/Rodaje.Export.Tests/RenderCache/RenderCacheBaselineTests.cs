using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export.Tests.Running;
using Rodaje.Media;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.Render.Shaders;
using Rodaje.TestMedia;
using Xunit.Abstractions;
using CompositeFormat = Rodaje.Render.CompositeFormat;

namespace Rodaje.Export.Tests.RenderCache;

[Collection(ExportGpuGroup.Name)]
[Trait("Categoria", "Rendimiento")]
public sealed class RenderCacheBaselineTests(ITestOutputHelper output)
{
    private const int FrameRate = 30;
    private const int FrameCount = 150;
    private static readonly int Threads = Math.Min(8, Environment.ProcessorCount);
    private static readonly Candidate[] SoftwareCandidates =
    [
        new("ProRes 422 HQ", "prores_ks", "yuv422p10le", "mov", ["-profile:v", "3"]),
        new("ProRes 4444", "prores_ks", "yuv444p10le", "mov", ["-profile:v", "4"]),
        new("DNxHR HQX", "dnxhd", "yuv422p10le", "mov", ["-profile:v", "dnxhr_hqx"]),
        new("FFV1", "ffv1", "yuv444p10le", "mkv", ["-level", "3", "-g", "1"]),
    ];

    // El equivalente de HardwareFact para medir también CPU y WARP en equipos sin D3D11VA.
    [FfmpegLibraryFact(LongRunning = true)]
    public void Medir_decodificacion_fidelidad_y_composicion_1080p() => Measure(1920, 1080);

    [FfmpegLibraryFact(LongRunning = true)]
    public void Medir_decodificacion_fidelidad_y_composicion_4K() => Measure(3840, 2160);

    private void Measure(int width, int height)
    {
        string directory = Directory.CreateTempSubdirectory($"rodaje-cache-F0-{height}-").FullName;
        string resultPath = Path.Combine(directory, "resultados.md");
        var report = new Report(output, resultPath);
        var total = Stopwatch.StartNew();
        using var process = Process.GetCurrentProcess();
        report.Write($"# Render en caché · F0 · {width}×{height}p30\n");
        report.Write($"Fecha UTC: {DateTime.UtcNow:O}; CPU lógicos: {Environment.ProcessorCount}; hilos de códec: {Threads}; prioridad: {process.PriorityClass}.");
        report.Write($"Resultados: {resultPath}");
        var mediaPaths = new List<string>();
        bool completed = false;
        try
        {
            // La misma instalación LGPL que cargó el atributo, sin recurrir a un ffmpeg del PATH.
            var ffmpeg = new FfmpegTool(Path.Combine(FfmpegLibrary.Directory!, "ffmpeg.exe"), "FFmpeg del entorno");
            report.Write($"{ffmpeg.Version}; bibliotecas: {FfmpegLibrary.VersionInfo}; ejecutable: {ffmpeg.ExecutablePath}.");
            string configuration = ReadText(ffmpeg, ["-hide_banner", "-buildconf"]);
            Assert.DoesNotContain("--enable-gpl", configuration, StringComparison.Ordinal);
            Assert.DoesNotContain("--enable-nonfree", configuration, StringComparison.Ordinal);
            using GpuDevice? gpu = GpuDevice.TryCreate(out string? gpuFailure);
            report.Write(gpu is null ? $"Sin GPU: {gpuFailure}" : $"GPU: {gpu.AdapterName}; D3D11VA: {gpu.SupportsHardwareDecoding}.");
            List<Candidate> candidates = [.. SoftwareCandidates];
            Candidate? hevc = FindHevc(ffmpeg, directory, report);
            if (hevc is not null)
            {
                candidates.Add(hevc);
            }

            report.Write("\n## Decodificación\n");
            report.Write("testsrc2, 5 s, 150 cuadros. Measure excluye el primer cuadro; apertura y primer cuadro se miden aparte. Seek hacia atrás de 120 a 30, confirmado por el contador del decodificador. Una muestra por ruta, caché de disco caliente; no es latencia de disco en frío.");
            report.Write("\n| Códec | Ruta real | fps | ×30 fps | ≥1,5× | Abrir ms | Primer cuadro ms | Seek + cuadro ms | MiB | Codificar s |");
            report.Write("|---|---|---:|---:|---|---:|---:|---:|---:|---:|");
            string? scenePath = null;
            double sceneCpuRatio = 0;
            foreach (Candidate candidate in candidates)
            {
                if (!ffmpeg.IsEncoderListed(candidate.Encoder))
                {
                    report.Write($"| {candidate.Name} | Omitido: {candidate.Encoder} no está en el FFmpeg LGPL | — | — | — | — | — | — | — | — |");
                    continue;
                }

                string path = Path.Combine(directory, $"{candidate.Encoder}-{candidates.IndexOf(candidate)}.{candidate.Extension}");
                mediaPaths.Add(path);
                var encoding = Stopwatch.StartNew();
                ffmpeg.Run(["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i",
                    Invariant($"testsrc2=size={width}x{height}:rate={FrameRate}:duration=5"),
                    "-an", .. EncoderArguments(candidate), path]);
                encoding.Stop();
                VideoStreamInfo info = MediaProbe.Probe(path).Video!;
                Assert.Equal(FrameCount, info.FrameCount);
                Assert.Equal(FrameRate, info.FrameRate.ToDouble());
                Assert.Equal(width, info.Width);
                Assert.Equal(height, info.Height);
                if (candidate == hevc)
                {
                    Assert.Equal(10, info.BitDepth);
                    IReadOnlyDictionary<char, int> types = ffmpeg.CountPictureTypes(path)!;
                    Assert.Equal(FrameCount, types.GetValueOrDefault('I'));
                    Assert.Equal(FrameCount, types.Values.Sum());
                }

                double cpuRatio = MeasureDecode(path, candidate.Name, null, report, encoding.Elapsed.TotalSeconds);
                if (scenePath is null || candidate == hevc)
                {
                    scenePath = path;
                    sceneCpuRatio = cpuRatio;
                }

                if (candidate == hevc)
                {
                    if (gpu?.SupportsHardwareDecoding == true)
                    {
                        MeasureDecode(path, candidate.Name, gpu, report, encoding.Elapsed.TotalSeconds);
                    }
                    else
                    {
                        report.Write($"| {candidate.Name} | D3D11VA omitido: no disponible | — | — | — | — | — | — | — | — |");
                    }
                }
            }

            MeasureFidelity(ffmpeg, width, height, directory, report, mediaPaths);
            if (scenePath is not null)
            {
                MeasureScenes(scenePath, sceneCpuRatio, width, height, hevc is not null, gpu, report);
            }
            else
            {
                report.Write("\nComposición omitida: falta una fuente codificable.");
            }

            report.Write("\nLos umbrales de D-060 (1,5× y 45 dB) se informan, no hacen fallar una medición. La decisión 4K necesita la pasada 4K; una medida sin audio ni presentación no acredita reproducción en tiempo real.");
            completed = true;
        }
        finally
        {
            report.Write(completed ? "\nEstado: medición terminada." : "\nEstado: medición interrumpida por un fallo; tabla incompleta.");
            report.Write(Invariant($"\nTiempo total: {total.Elapsed.TotalSeconds:F1} s. Resultados: {resultPath}"));
            foreach (string path in mediaPaths)
            {
                File.Delete(path);
            }
        }
    }

    private static Candidate? FindHevc(FfmpegTool ffmpeg, string directory, Report report)
    {
        foreach (string encoder in new[] { "hevc_nvenc", "hevc_amf", "hevc_qsv" })
        {
            if (!ffmpeg.IsEncoderListed(encoder))
            {
                report.Write($"HEVC: {encoder} no está incluido.");
                continue;
            }

            string[] options = encoder switch
            {
                "hevc_nvenc" => ["-preset", "p4", "-rc", "constqp", "-qp", "10"],
                "hevc_amf" => ["-rc", "cqp", "-qp_i", "10", "-qp_p", "10"],
                _ => ["-global_quality", "10"],
            };
            // NVENC necesita g=0 para anular el intervalo P del preset y producir solo cuadros I.
            string gop = encoder == "hevc_nvenc" ? "0" : "1";
            var candidate = new Candidate($"HEVC Main 10 intra ({encoder})", encoder, "p010le", "mp4",
                ["-profile:v", "main10", "-g", gop, "-bf", "0", .. options]);
            string path = Path.Combine(directory, "sonda-hevc.mp4");
            try
            {
                ffmpeg.Run(["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i",
                    "testsrc2=size=320x240:rate=30:duration=0.1", "-an", .. EncoderArguments(candidate), path]);
                return candidate;
            }
            catch (FfmpegException ex)
            {
                report.Write($"HEVC {encoder} no utilizable con Main 10 intra: {ex.Message.ReplaceLineEndings(" ")}");
            }
            finally
            {
                File.Delete(path);
            }
        }

        report.Write("HEVC Main 10 intra omitido: ningún codificador de hardware utilizable.");
        return null;
    }

    private static double MeasureDecode(string path, string name, GpuDevice? gpu, Report report, double encodingSeconds)
    {
        var options = new VideoDecoderOptions { Device = gpu, AllowHardwareDecoding = gpu is not null, SoftwareThreads = Threads };
        long start = Stopwatch.GetTimestamp();
        using VideoDecoder decoder = VideoDecoder.Open(path, options);
        double openMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        start = Stopwatch.GetTimestamp();
        using (DecodedFrame? first = decoder.GetFrame(0))
        {
            Assert.NotNull(first);
            Assert.Equal(0, first.Index);
        }

        double firstMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        using (DecodedFrame? far = decoder.GetFrame(120))
        {
            Assert.NotNull(far);
            Assert.Equal(120, far.Index);
        }

        long seeks = decoder.Statistics.Seeks;
        start = Stopwatch.GetTimestamp();
        using (DecodedFrame? sought = decoder.GetFrame(30))
        {
            Assert.NotNull(sought);
            Assert.Equal(30, sought.Index);
        }

        double seekMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Assert.True(decoder.Statistics.Seeks > seeks, "La medida debe incluir un seek real.");
        string route = gpu is null ? "CPU" : decoder.UsesHardwareDecoding ? "D3D11VA" : "CPU + subida (respaldo)";
        bool hardware = decoder.UsesHardwareDecoding;
        decoder.Dispose();
        double ratio = DecodeSpeedProbe.Measure(path, options, frames: FrameCount);
        report.Write(Invariant($"| {name} | {route} | {ratio * FrameRate:F2} | {ratio:F2}× | {(ratio >= 1.5 ? "Sí" : "No")} | {openMs:F2} | {firstMs:F2} | {seekMs:F2} | {new FileInfo(path).Length / 1048576.0:F2} | {encodingSeconds:F2} |"));
        if (gpu is not null && !hardware)
        {
            report.Write("D3D11VA cayó a software; esta fila no cuenta como medida de hardware.");
        }

        return ratio;
    }

    private static void MeasureFidelity(FfmpegTool ffmpeg, int width, int height, string directory, Report report, List<string> mediaPaths)
    {
        report.Write("\n## Fidelidad\n");
        report.Write("Imagen 4:4:4 de 10 bits: barras saturadas, panel negro y contornos blancos de 1 px (sin depender de fuentes). PQ se convierte de BT.709 a BT.2020/PQ con zscale, blanco de 203 nits. Comparación YUV444p10le frente al original, antes de FrameUploader; incluye la pérdida de submuestreo 4:2:2. PSNR sobre las tres componentes de códigos de 10 bits, no luminancia lineal. Error máximo en códigos /1023. No mide la pérdida adicional del compositor al subir 4:2:2.");
        report.Write("\n| Imagen | Códec | PSNR dB | ≥45 dB | Error máximo /1023 |");
        report.Write("|---|---|---:|---|---:|");
        foreach (bool hdr in new[] { false, true })
        {
            string label = hdr ? "HDR PQ BT.2020" : "SDR BT.709";
            string raw = Path.Combine(directory, hdr ? "original-pq.yuv" : "original-sdr.yuv");
            mediaPaths.Add(raw);
            string source = Invariant($"smptebars=size={width}x{height}:rate=30,format=yuv444p10le,")
                + "drawbox=x=101:y=101:w=600:h=180:color=black:t=fill,"
                + "drawbox=x=121:y=121:w=97:h=141:color=white:t=1,"
                + "drawbox=x=241:y=121:w=97:h=141:color=white:t=1,"
                + "drawbox=x=361:y=121:w=97:h=141:color=white:t=1,"
                + "drawbox=x=481:y=121:w=97:h=141:color=white:t=1,"
                + "drawbox=x=701:y=301:w=1:h=300:color=red:t=fill,"
                + "drawbox=x=703:y=301:w=1:h=300:color=blue:t=fill";
            if (hdr)
            {
                source += ",zscale=primariesin=bt709:transferin=bt709:matrixin=bt709:rangein=limited:"
                    + "primaries=bt2020:transfer=smpte2084:matrix=bt2020nc:range=limited:npl=203,format=yuv444p10le";
            }

            ffmpeg.Run(["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i", source,
                "-frames:v", "1", "-pix_fmt", "yuv444p10le", "-f", "rawvideo", raw]);
            byte[] original = File.ReadAllBytes(raw);
            Assert.Equal(width * height * 6, original.Length);
            foreach (Candidate candidate in SoftwareCandidates.Take(2))
            {
                if (!ffmpeg.IsEncoderListed(candidate.Encoder))
                {
                    report.Write($"| {label} | {candidate.Name} omitido: falta {candidate.Encoder} | — | — | — |");
                    continue;
                }

                string encoded = Path.Combine(directory, $"fidelidad-{hdr}-{candidate.PixelFormat}.mov");
                mediaPaths.Add(encoded);
                string[] input = ["-f", "rawvideo", "-pixel_format", "yuv444p10le", "-video_size", Invariant($"{width}x{height}"), "-framerate", "30", "-i", raw];
                string[] color = ["-color_range", "tv", "-color_primaries", hdr ? "bt2020" : "bt709", "-color_trc", hdr ? "smpte2084" : "bt709",
                    "-colorspace", hdr ? "bt2020nc" : "bt709"];
                string parameters = hdr
                    ? "setparams=range=limited:color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc"
                    : "setparams=range=limited:color_primaries=bt709:color_trc=bt709:colorspace=bt709";
                ffmpeg.Run(["-hide_banner", "-loglevel", "error", "-nostdin", "-y", .. input, "-frames:v", "1",
                    "-vf", parameters, .. EncoderArguments(candidate), .. color, encoded]);
                VideoStreamInfo info = MediaProbe.Probe(encoded).Video!;
                Assert.True(info.BitDepth >= 10, "La fidelidad debe medirse con al menos 10 bits.");
                if (hdr)
                {
                    Assert.Equal("PQ", info.HdrTransfer);
                }

                byte[] decoded = ReadBytes(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", encoded,
                    "-frames:v", "1", "-pix_fmt", "yuv444p10le", "-f", "rawvideo", "-"]);
                Assert.Equal(original.Length, decoded.Length);
                long squaredError = 0;
                int maximum = 0;
                for (int offset = 0; offset < original.Length; offset += 2)
                {
                    int difference = Math.Abs(BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(offset, 2))
                        - BinaryPrimitives.ReadUInt16LittleEndian(decoded.AsSpan(offset, 2)));
                    maximum = Math.Max(maximum, difference);
                    squaredError += difference * difference;
                }

                double mse = squaredError / (original.Length / 2.0);
                double psnr = mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(1023.0 * 1023 / mse);
                report.Write(Invariant($"| {label} | {candidate.Name} | {psnr:F3} | {(psnr >= 45 ? "Sí" : "No")} | {maximum} |"));
            }
        }
    }

    private static void MeasureScenes(string path, double cpuRatio, int width, int height, bool hevc, GpuDevice? gpu, Report report)
    {
        report.Write("\n## Composición (arnés Compositor + GpuTimer)\n");
        report.Write($"Fuente: {Path.GetFileName(path)}. 10 cuadros de calentamiento, hasta 60 medidos, mediana/p95. Decodificación CPU estimada a partir de Measure × número de capas; GetFrame incluye el envío de la subida (no su espera en GPU). Componer usa marcas D3D11, con espera de terminación entre cuadros. Shaders compilados antes de medir. Audio y presentación no se miden en este arnés sin ventana.");
        report.Write("\n| Escena | Dispositivo | Ruta real | CPU decodificar ms/cuadro | GetFrame + envío subida mediana ms | Componer mediana ms | Componer p95 ms |");
        report.Write("|---|---|---|---:|---:|---:|---:|");
        if (height == 2160)
        {
            if (hevc && gpu is not null)
            {
                MeasureScene("A: 4 capas 4K, 6 efectos/capa", path, width, height, 4, true, true, cpuRatio, gpu, report);
            }
            else
            {
                report.Write("Escena A omitida: falta un codificador HEVC de hardware utilizable o un dispositivo GPU.");
            }

            report.Write("Escena C se mide en la variante 1080p.");
            return;
        }

        double? hardware = gpu is { IsSoftware: false }
            ? MeasureScene("C: 2 capas 1080p, color", path, width, height, 2, false, false, cpuRatio, gpu, report)
            : null;
        using GpuDevice warp = GpuDevice.CreateWarp();
        double software = MeasureScene("C: 2 capas 1080p, color", path, width, height, 2, false, false, cpuRatio, warp, report);
        report.Write(hardware is { } hardwareMs
            ? Invariant($"Factor sin GPU (componer C, WARP/GPU): {software / hardwareMs:F2}×. La decodificación es CPU en ambos casos.")
            : "Factor WARP/GPU no disponible: no hay GPU de hardware.");
        report.Write("Escena A se mide en la variante 4K. Escenas B, D y E pendientes: requieren ampliar el arnés de reproducción.");
    }

    private static double MeasureScene(string name, string path, int width, int height, int count, bool sixEffects,
        bool hardwareDecode, double cpuRatio, GpuDevice gpu, Report report)
    {
        var decoders = new List<VideoDecoder>();
        using var compositor = new Compositor(gpu, width, height, CompositeFormat.Rgba16Float, new ShaderCache(null));
        using var timer = new GpuTimer(gpu);
        var decodeTimes = new List<double>();
        var composeTimes = new List<double>();
        try
        {
            for (int index = 0; index < count; index++)
            {
                decoders.Add(VideoDecoder.Open(path, new VideoDecoderOptions
                {
                    Device = gpu, AllowHardwareDecoding = hardwareDecode, SoftwareThreads = Threads,
                    CompactReferencePool = true, ExtraHardwareFrames = 2,
                }));
            }

            for (int frameIndex = 0; frameIndex < 70; frameIndex++)
            {
                var layers = new List<LayerFrame>();
                using var frame = new CompositionFrame(Time.FromFrames(frameIndex, Rational.Fps30), width, height, layers);
                long start = Stopwatch.GetTimestamp();
                for (int index = 0; index < count; index++)
                {
                    DecodedFrame? decoded = decoders[index].GetFrame(frameIndex);
                    Assert.NotNull(decoded);
                    layers.Add(new LayerFrame(index, Invariant($"capa-{index}"), decoded,
                        LayerTransform.Identity with { Opacity = index == 0 ? 1 : 0.85 })
                    {
                        Effects = SceneEffects(sixEffects),
                    });
                }

                double decodeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                timer.Begin();
                CompositeResult result = frameIndex == 0 ? compositor.RenderStrict(frame) : compositor.Render(frame);
                timer.End();
                double? composeMs = timer.WaitMilliseconds(TimeSpan.FromSeconds(30));
                Assert.Equal(count, result.LayersDrawn);
                Assert.Equal(0, result.LayersSkipped);
                Assert.Equal(0, result.EffectsSkipped);
                Assert.Equal(0, result.MissingContent);
                if (frameIndex >= 10 && composeMs is { } measured)
                {
                    decodeTimes.Add(decodeMs);
                    composeTimes.Add(measured);
                }
            }

            Assert.True(composeTimes.Count >= 30, "Demasiadas mediciones disjuntas o sin respuesta de D3D11.");
            decodeTimes.Sort();
            composeTimes.Sort();
            double median = Percentile(composeTimes, 0.5);
            string route = decoders.All(d => d.UsesHardwareDecoding) ? "D3D11VA" : "CPU + subida";
            report.Write(Invariant($"| {name} | {gpu.AdapterName} | {route} | {count * 1000 / (cpuRatio * FrameRate):F3} | {Percentile(decodeTimes, 0.5):F3} | {median:F3} | {Percentile(composeTimes, 0.95):F3} |"));
            return median;
        }
        finally
        {
            foreach (VideoDecoder decoder in decoders)
            {
                decoder.Dispose();
            }
        }
    }

    private static LayerEffect[] SceneEffects(bool six)
    {
        LayerEffect color = Effect(BuiltInEffects.Color, (BuiltInEffects.ColorParameters.Exposure, 0.2),
            (BuiltInEffects.ColorParameters.Contrast, 0.15), (BuiltInEffects.ColorParameters.Saturation, 0.2));
        if (!six)
        {
            return [color];
        }

        LayerEffect blur = Effect(BuiltInEffects.Blur, (BuiltInEffects.BlurParameters.Radius, 12));
        EffectDefinition definition = EffectRegistry.Find(BuiltInEffects.Blur)!;
        EffectValues values = blur.Values;
        int slot = definition.Parameters.Length;
        foreach (OptionDefinition option in definition.Options.Where(o => o.Kind == OptionKind.Choice))
        {
            values[slot++] = option.Choices.IndexOf(option.Name == BuiltInEffects.BlurOptions.Kind ? "lens" : option.DefaultValue);
        }

        return
        [
            color,
            Effect(BuiltInEffects.HslQualifier, (BuiltInEffects.HslQualifierParameters.HueCenter, 30),
                (BuiltInEffects.HslQualifierParameters.HueShift, 10), (BuiltInEffects.HslQualifierParameters.Saturation, 0.2)),
            new(EffectRegistry.Find(BuiltInEffects.Curves)!.Code, default,
                new ResourceKey("0,0;0.5,0.55;1,1||||0,0.5;0.3,0.45|0.3,0.3;0.6,0.6|")),
            blur with { Values = values },
            Effect(BuiltInEffects.Glow),
            Effect(BuiltInEffects.Grain),
        ];
    }

    private static LayerEffect Effect(string type, params (string Name, double Value)[] parameters)
    {
        EffectDefinition definition = EffectRegistry.Find(type)!;
        var values = new EffectValues();
        foreach (ParameterDefinition parameter in definition.Parameters)
        {
            values[parameter.Slot] = (float)parameter.DefaultValue;
        }

        foreach ((string name, double value) in parameters)
        {
            values[definition.FindParameter(name)!.Slot] = (float)value;
        }

        return new LayerEffect(definition.Code, values, null);
    }

    private static string[] EncoderArguments(Candidate candidate) =>
        ["-c:v", candidate.Encoder, "-threads:v", Threads.ToString(CultureInfo.InvariantCulture), "-pix_fmt", candidate.PixelFormat, .. candidate.Options];

    private static byte[] ReadBytes(FfmpegTool ffmpeg, string[] arguments)
    {
        using var memory = new MemoryStream();
        ffmpeg.Run(arguments, readOutput: stream => stream.CopyTo(memory));
        return memory.ToArray();
    }

    private static string ReadText(FfmpegTool ffmpeg, string[] arguments) => Encoding.UTF8.GetString(ReadBytes(ffmpeg, arguments));

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    private static double Percentile(List<double> sorted, double fraction) => sorted[(int)Math.Round((sorted.Count - 1) * fraction)];

    private sealed record Candidate(string Name, string Encoder, string PixelFormat, string Extension, string[] Options);

    private sealed class Report(ITestOutputHelper output, string path)
    {
        public void Write(string line)
        {
            output.WriteLine(line);
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
    }
}
