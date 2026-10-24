using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Rodaje.App.Services.Modules;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Ai;
using Rodaje.Media.Proxies;
using Rodaje.Media.Video;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Views.Vision;

/// <summary>El video gris que dejó «Quitar fondo»: su archivo y el instante del origen de su primer cuadro.</summary>
internal sealed record MatteVideo(string Path, Time Start, int Frames, int Width, int Height);

/// <summary>Una caja detectada en un cuadro de análisis, normalizada al cuadro (esquina de arriba a la izquierda y tamaño).</summary>
internal readonly record struct DetectedBox(double X, double Y, double Width, double Height, double Score);

/// <summary>Un keyframe del reencuadre: centro del recorte (fracción del cuadro fuente) y acercamiento sobre el recorte máximo.</summary>
internal readonly record struct ReframeKey(double Seconds, double X, double Y, double Zoom);

/// <summary>Lo que propone el reencuadre: los keyframes y en qué fracción de los cuadros el sujeto queda dentro.</summary>
internal sealed record ReframePlan(IReadOnlyList<ReframeKey> Keys, double Inside, int Frames);

/// <summary>
/// «Quitar fondo» y «Reencuadre automático» sin interfaz (modulo-vision). El editor decodifica los cuadros y el módulo
/// devuelve máscaras o cajas; nada edita el proyecto hasta aplicar la propuesta.
/// </summary>
internal static class VisionTools
{
    public const string RemoveBackgroundFunction = "quitar-fondo";
    public const string DetectFunction = "detectar";
    public const string ReframeFunction = "reencuadre";

    /// <summary>Cuadros por llamada a «quitar-fondo» (pares entrada/salida).</summary>
    public const int MaskBatch = 8;

    /// <summary>Cuadros por llamada a «detectar».</summary>
    public const int DetectBatch = 16;

    /// <summary>Cuadros por segundo del análisis del reencuadre: la trayectoria se suaviza, no hace falta más.</summary>
    public const double ReframeRate = 6;

    /// <summary>Lado mayor del análisis del reencuadre.</summary>
    public const int ReframeSide = 640;

    /// <summary>Lado mayor de la máscara: 512 con MODNet, 1024 con BiRefNet_lite («Calidad alta»).</summary>
    public static int MaskSide(bool highQuality) => highQuality ? 1024 : 512;

    /// <summary>Los cuadros del medio que cubre el clip: el primero y cuántos, a la tasa del medio.</summary>
    public static (long First, int Count) FrameSpan(Clip clip, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(clip);
        double fps = Fps(frameRate);
        long first = (long)Math.Floor((clip.SourceIn.ToSeconds() * fps) + 1e-6);
        long end = (long)Math.Ceiling((clip.SourceOut.ToSeconds() * fps) - 1e-6);
        return (first, (int)Math.Max(1, end - first));
    }

    public static double Fps(Rational rate) => rate.Num > 0 && rate.Den > 0 ? (double)rate.Num / rate.Den : 30;

    /// <summary>Cuánto tardará «Quitar fondo» en este equipo (con su aviso si la calidad alta no cabe), o <c>null</c> sin perfil de IA.</summary>
    public static AiEstimate? EstimateRemoveBackground(ModuleManager modules, Clip clip, Rational frameRate, bool highQuality)
    {
        ArgumentNullException.ThrowIfNull(modules);
        return modules.Ai is { } ai ? AiEstimator.ForRemoveBackground(ai.Plan(), FrameSpan(clip, frameRate).Count, highQuality, ai.Rates) : null;
    }

    /// <summary>Cuánto tardará el análisis del reencuadre, o <c>null</c> sin perfil de IA.</summary>
    public static AiEstimate? EstimateReframe(ModuleManager modules, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(clip);
        int count = Math.Clamp((int)Math.Ceiling(Math.Max(clip.SourceDuration.ToSeconds(), 1e-3) * ReframeRate), 1, 20_000);
        return modules.Ai is { } ai ? AiEstimator.ForDetect(ai.Plan(), count, ai.Rates) : null;
    }

    /// <summary>
    /// Pide al módulo la máscara de cada cuadro del clip y la guarda como video gris FFV1 en <paramref name="folder"/>. Es un
    /// trabajo de Trabajos (avance, cancelar); el archivo aparece entero o no aparece.
    /// </summary>
    public static Task<MatteVideo> RemoveBackgroundAsync(ModuleManager modules, ModuleDescriptor module, FfmpegExecutable ffmpeg, string mediaPath,
        Clip clip, Rational frameRate, bool highQuality, string folder, string title, CancellationToken cancellationToken, Action<ModuleJob>? started = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(ffmpeg);
        (long first, int count) = FrameSpan(clip, frameRate);
        double fps = Fps(frameRate);
        AiPlan? plan = modules.Ai?.Plan();
        int batchSize = plan?.Vision.FramesPerCall ?? MaskBatch;
        ModuleDemand? demand = plan is null ? null
            : AiParameters.Demand(AiParameters.RemoveBackgroundDevice(plan, highQuality), highQuality ? plan.Vision.HighVramMb : plan.Vision.VramMb);
        return modules.RunAsync(module.Id, title, demand, async call =>
        {
            started?.Invoke(call.Job);
            CancellationToken token = call.CancellationToken;
            var watch = Stopwatch.StartNew();
            int side = highQuality || plan is null ? MaskSide(highQuality) : plan.Vision.MaskSide;
            using ThumbnailDecoder decoder = await Task.Run(() => ThumbnailDecoder.Open(mediaPath), token).ConfigureAwait(false);
            long frameBytes = ModuleMedia.FrameBytes(side);
            SharedMemoryRegion region = call.CreateRegion(batchSize * (frameBytes + (frameBytes / 4)));
            Directory.CreateDirectory(folder);
            string final = Path.Combine(folder, $"fondo-{Guid.NewGuid():N}.mkv");
            string temporary = final + ".rodaje-tmp";
            Process? encoder = null;
            Task<string>? errors = null;
            bool done = false;
            int width = 0, height = 0;
            string session = Guid.NewGuid().ToString("N");
            try
            {
                for (int start = 0; start < count; start += batchSize)
                {
                    token.ThrowIfCancellationRequested();
                    int batch = Math.Min(batchSize, count - start);
                    var pairs = new List<SharedBufferRef>(batch * 2);
                    long at = 0;
                    for (int i = 0; i < batch; i++)
                    {
                        Time time = Time.FromSeconds((first + start + i + 0.5) / fps);
                        SharedBufferRef frame = await Task.Run(() => ModuleMedia.WriteFrame(decoder, region, at, time, side, token), token).ConfigureAwait(false)
                            ?? throw new IOException("El video no tiene fotogramas que leer en el tramo del clip.");
                        VideoLayout layout = frame.Video!;
                        (width, height) = width == 0 ? (layout.Width, layout.Height) : (width, height);
                        if (layout.Width != width || layout.Height != height)
                        {
                            throw new IOException("El video cambió de tamaño a mitad del clip.");
                        }

                        pairs.Add(frame);
                        at += frame.Length;
                        pairs.Add(region.Describe(at, (long)width * height, new VideoLayout(width, height, width, PixelFormats.Gray8)));
                        at += (long)width * height;
                    }

                    if (encoder is null)
                    {
                        encoder = StartEncoder(ffmpeg, width, height, frameRate, temporary);
                        errors = encoder.StandardError.ReadToEndAsync(CancellationToken.None);
                    }

                    using JsonDocument parameters = plan is not null ? AiParameters.RemoveBackground(plan, highQuality, session, start == 0)
                        : JsonDocument.Parse(string.Create(CultureInfo.InvariantCulture,
                        $$"""{"calidad":"{{(highQuality ? "alta" : "normal")}}","sesion":"{{session}}","reiniciar":{{(start == 0 ? "true" : "false")}}}"""));
                    double from = 0.97 * start / count, to = 0.97 * (start + batch) / count;
                    ModuleResult result = await call.InvokeAsync(RemoveBackgroundFunction, parameters.RootElement, pairs, from, to).ConfigureAwait(false);
                    if (result.Buffers.Count != batch)
                    {
                        throw new ModuleProtocolException("«quitar-fondo» devolvió otra cantidad de máscaras.");
                    }

                    Stream stdin = encoder.StandardInput.BaseStream;
                    try
                    {
                        foreach (SharedBufferRef mask in result.Buffers)
                        {
                            Span<byte> gray = call.Map(mask);
                            if (gray.Length != (long)width * height)
                            {
                                throw new ModuleProtocolException("«quitar-fondo» devolvió una máscara de otro tamaño.");
                            }

                            stdin.Write(gray);
                        }
                    }
                    catch (IOException)
                    {
                        encoder.WaitForExit(5000);
                        throw new IOException($"ffmpeg.exe se detuvo: {Tail(await errors!.ConfigureAwait(false))}");
                    }
                }

                call.Report(0.97, "Guardando la máscara");
                encoder!.StandardInput.BaseStream.Close();
                await encoder.WaitForExitAsync(token).ConfigureAwait(false);
                string stderr = await errors!.ConfigureAwait(false);
                if (encoder.ExitCode != 0 || !File.Exists(temporary))
                {
                    throw new IOException($"ffmpeg.exe terminó con código {encoder.ExitCode}: {Tail(stderr)}");
                }

                File.Move(temporary, final);
                done = true;
                if (plan is not null)
                {
                    modules.Ai?.Rates.Record(AiEstimator.ForRemoveBackground(plan, count, highQuality).Key, count, watch.Elapsed.TotalSeconds);
                }

                return new MatteVideo(final, Time.FromSeconds(first / fps), count, width, height);
            }
            finally
            {
                if (!done)
                {
                    Kill(encoder);
                    TryDelete(temporary);
                }

                encoder?.Dispose();
            }
        }, cancellationToken);
    }

    /// <summary>Argumentos de <c>ffmpeg.exe</c>: gris de 8 bits por la entrada estándar → FFV1 intra en Matroska.</summary>
    public static IReadOnlyList<string> EncoderArguments(int width, int height, Rational frameRate, string output) =>
    [
        "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
        "-f", "rawvideo", "-pix_fmt", "gray", "-s", $"{width}x{height}", "-framerate", $"{frameRate.Num}/{frameRate.Den}", "-i", "pipe:0",
        "-c:v", "ffv1", "-level", "3", "-g", "1", "-slices", "4", "-pix_fmt", "gray", "-f", "matroska", output,
    ];

    /// <summary>
    /// Detecta rostros o personas en cuadros del clip a <see cref="ReframeRate"/> y pide al módulo la trayectoria del recorte
    /// con la forma <paramref name="aspect"/> (ancho / alto). Es un trabajo de Trabajos.
    /// </summary>
    public static Task<ReframePlan> ReframeAsync(ModuleManager modules, ModuleDescriptor module, string mediaPath, Clip clip, double aspect,
        bool faces, double smoothing, string title, CancellationToken cancellationToken, Action<ModuleJob>? started = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(clip);
        double startSeconds = clip.SourceIn.ToSeconds();
        double span = Math.Max(clip.SourceDuration.ToSeconds(), 1e-3);
        int count = Math.Clamp((int)Math.Ceiling(span * ReframeRate), 1, 20_000);
        AiPlan? plan = modules.Ai?.Plan();
        ModuleDemand? demand = plan is null ? null : AiParameters.Demand(plan.Vision.Device, plan.Vision.VramMb);
        return modules.RunAsync(module.Id, title, demand, async call =>
        {
            started?.Invoke(call.Job);
            CancellationToken token = call.CancellationToken;
            var watch = Stopwatch.StartNew();
            using ThumbnailDecoder decoder = await Task.Run(() => ThumbnailDecoder.Open(mediaPath), token).ConfigureAwait(false);
            SharedMemoryRegion region = call.CreateRegion(DetectBatch * ModuleMedia.FrameBytes(ReframeSide));
            var frames = new List<(double Seconds, IReadOnlyList<DetectedBox> Boxes)>(count);
            int sourceWidth = 0, sourceHeight = 0;
            using JsonDocument detect = plan is not null ? AiParameters.Detect(plan, faces)
                : JsonDocument.Parse(faces ? """{"objetivo":"rostros"}""" : """{"objetivo":"personas"}""");
            for (int start = 0; start < count; start += DetectBatch)
            {
                token.ThrowIfCancellationRequested();
                int batch = Math.Min(DetectBatch, count - start);
                var buffers = new List<SharedBufferRef>(batch);
                var times = new List<double>(batch);
                long at = 0;
                for (int i = 0; i < batch; i++)
                {
                    double seconds = startSeconds + (span * (start + i + 0.5) / count);
                    if (await Task.Run(() => ModuleMedia.WriteFrame(decoder, region, at, Time.FromSeconds(seconds), ReframeSide, token), token)
                        .ConfigureAwait(false) is not { } frame)
                    {
                        continue;
                    }

                    (sourceWidth, sourceHeight) = (frame.Video!.Width, frame.Video.Height);
                    buffers.Add(frame);
                    times.Add(seconds);
                    at += frame.Length;
                }

                if (buffers.Count == 0)
                {
                    continue;
                }

                ModuleResult result = await call.InvokeAsync(DetectFunction, detect.RootElement, buffers, 0.9 * start / count, 0.9 * (start + batch) / count)
                    .ConfigureAwait(false);
                IReadOnlyList<IReadOnlyList<DetectedBox>> boxes = ReadDetections(result.Value, buffers.Count);
                for (int i = 0; i < buffers.Count; i++)
                {
                    frames.Add((times[i], boxes[i]));
                }
            }

            if (frames.Count == 0)
            {
                throw new IOException("El video no tiene fotogramas que leer en el tramo del clip.");
            }

            call.Report(0.92, "Calculando el recorrido");
            using JsonDocument request = JsonDocument.Parse(PlanRequest(aspect, sourceWidth, sourceHeight, frames, smoothing));
            ModuleResult planned = await call.InvokeAsync(ReframeFunction, request.RootElement, null, 0.92, 1).ConfigureAwait(false);
            if (plan is not null)
            {
                modules.Ai?.Rates.Record(AiEstimator.ForDetect(plan, count).Key, count, watch.Elapsed.TotalSeconds);
            }

            return ReadPlan(planned.Value, frames.Count);
        }, cancellationToken);
    }

    /// <summary>El JSON de «reencuadre»: forma de destino, tamaño del análisis y las cajas de cada cuadro con su instante.</summary>
    public static string PlanRequest(double aspect, int width, int height, IReadOnlyList<(double Seconds, IReadOnlyList<DetectedBox> Boxes)> frames,
        double smoothing)
    {
        ArgumentNullException.ThrowIfNull(frames);
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("aspecto", aspect);
            json.WriteStartObject("fuente");
            json.WriteNumber("ancho", width);
            json.WriteNumber("alto", height);
            json.WriteEndObject();
            json.WriteNumber("suavizado", Math.Clamp(smoothing, 0, 1));
            json.WriteStartArray("cuadros");
            foreach ((double seconds, IReadOnlyList<DetectedBox> boxes) in frames)
            {
                json.WriteStartObject();
                json.WriteNumber("t", Math.Round(seconds, 4));
                json.WriteStartArray("cajas");
                foreach (DetectedBox box in boxes)
                {
                    json.WriteStartObject();
                    json.WriteNumber("x", Math.Round(box.X, 4));
                    json.WriteNumber("y", Math.Round(box.Y, 4));
                    json.WriteNumber("w", Math.Round(box.Width, 4));
                    json.WriteNumber("h", Math.Round(box.Height, 4));
                    json.WriteNumber("p", Math.Round(box.Score, 3));
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Las cajas de «detectar», una lista por cuadro; lo que no se entiende se descarta.</summary>
    public static IReadOnlyList<IReadOnlyList<DetectedBox>> ReadDetections(JsonElement? value, int frames)
    {
        if (value is not { ValueKind: JsonValueKind.Object } v || !v.TryGetProperty("cuadros", out JsonElement list)
            || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() != frames)
        {
            throw new ModuleProtocolException("«detectar» devolvió otra cantidad de fotogramas.");
        }

        var all = new List<IReadOnlyList<DetectedBox>>(frames);
        foreach (JsonElement frame in list.EnumerateArray())
        {
            var boxes = new List<DetectedBox>();
            if (frame.ValueKind == JsonValueKind.Object && frame.TryGetProperty("cajas", out JsonElement cajas) && cajas.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement b in cajas.EnumerateArray().Take(64))
                {
                    if (Number(b, "x") is { } x && Number(b, "y") is { } y && Number(b, "w") is { } w && Number(b, "h") is { } h && w > 0 && h > 0)
                    {
                        boxes.Add(new DetectedBox(x, y, w, h, Number(b, "p") ?? 1));
                    }
                }
            }

            all.Add(boxes);
        }

        return all;
    }

    /// <summary>Los keyframes de «reencuadre», ordenados y acotados (el recorte nunca sale del cuadro).</summary>
    public static ReframePlan ReadPlan(JsonElement? value, int frames)
    {
        if (value is not { ValueKind: JsonValueKind.Object } v || !v.TryGetProperty("keyframes", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new ModuleProtocolException("«reencuadre» no devolvió keyframes.");
        }

        var keys = new List<ReframeKey>();
        foreach (JsonElement k in list.EnumerateArray().Take(10_000))
        {
            if (Number(k, "t") is { } t && Number(k, "x") is { } x && Number(k, "y") is { } y)
            {
                keys.Add(new ReframeKey(t, Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1), Math.Clamp(Number(k, "scale") ?? 1, 1, 10)));
            }
        }

        if (keys.Count == 0)
        {
            throw new ModuleProtocolException("«reencuadre» no devolvió keyframes.");
        }

        double inside = Number(v, "dentro") is { } d ? Math.Clamp(d, 0, 1) : double.NaN;
        return new ReframePlan([.. keys.OrderBy(k => k.Seconds)], inside, frames);
    }

    /// <summary>
    /// Un keyframe del recorte como <c>rodaje.transform</c> del clip en una secuencia de <paramref name="sequenceWidth"/> ×
    /// <paramref name="sequenceHeight"/>: la capa encaja en el cuadro y se agranda hasta que el recorte lo llena, y se corre
    /// para que el centro del recorte quede en el centro del cuadro.
    /// </summary>
    public static TransformKey ToTransform(ReframeKey key, int sourceWidth, int sourceHeight, int sequenceWidth, int sequenceHeight)
    {
        double aspect = (double)sequenceWidth / sequenceHeight;
        double cropWidth = (double)sourceWidth / sourceHeight > aspect ? sourceHeight * aspect : sourceWidth;
        cropWidth /= key.Zoom;
        double fit = Math.Min((double)sequenceWidth / sourceWidth, (double)sequenceHeight / sourceHeight);
        double scale = sequenceWidth / (fit * cropWidth);
        double k = scale * fit;
        return new TransformKey(Time.FromSeconds(key.Seconds), Math.Round(k * (0.5 - key.X) * sourceWidth, 2), Math.Round(k * (0.5 - key.Y) * sourceHeight, 2),
            Math.Round(scale, 5));
    }

    private static double? Number(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double d) && double.IsFinite(d) ? d : null;

    private static Process StartEncoder(FfmpegExecutable ffmpeg, int width, int height, Rational frameRate, string output)
    {
        var info = new ProcessStartInfo(ffmpeg.EnsureUnchanged().Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(ffmpeg.Path)!,
        };
        foreach (string argument in EncoderArguments(width, height, frameRate, output))
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info) ?? throw new IOException("ffmpeg.exe no arrancó.");
    }

    private static string Tail(string text) => text.Length > 400 ? text[^400..].Trim() : text.Trim();

    private static void Kill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
