using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Rodaje.App.Services.Modules;
using Rodaje.Media.Proxies;
using Rodaje.Modules.Protocol;
using ModuleCall = Rodaje.App.Services.Modules.ModuleCall;

namespace Rodaje.App.Views.Raw;

/// <summary>Una carpeta CinemaDNG: los cuadros en orden y, si hay uno solo, el WAV de la toma.</summary>
internal sealed record CinemaDngFolder(string Folder, IReadOnlyList<string> Frames, long LargestFrameBytes, string? Audio)
{
    public string Name => Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}

/// <summary>Cómo se revela al importar (lo que el diálogo deja elegir).</summary>
/// <param name="Balance"><c>camara</c> (el de la toma) o <c>luz-dia</c>.</param>
/// <param name="Exposure">Pasos (EV) en luz lineal.</param>
internal sealed record CinemaDngOptions(string Balance = "camara", double Exposure = 0, bool HighQuality = true);

/// <summary>Lo que quedó: el archivo intermedio y lo que dijo el módulo del primer cuadro.</summary>
internal sealed record CinemaDngResult(string Path, int Width, int Height, int Frames, int RateNumerator, int RateDenominator, string Camera);

/// <summary>
/// Importar una carpeta CinemaDNG como un medio, sin tocar el motor: el editor lee cada DNG a memoria compartida,
/// <c>modulo-raw</c> lo revela (lotes en paralelo, rgba16f con la curva Rec.709) y el <c>ffmpeg.exe</c> LGPL lo guarda
/// como ProRes 422 HQ de 10 bits (con el WAV de la toma, si hay) en la carpeta de medios generados. Ese archivo entra a la
/// biblioteca como cualquier video; para revelar con otros ajustes se vuelve a importar. Cancelar o un fallo no deja nada.
/// </summary>
internal static class CinemaDngImport
{
    public const string Function = "revelar";

    /// <summary>Lo que se reserva de memoria compartida como mucho (archivos y cuadros revelados de un lote).</summary>
    public const long MaxBatchBytes = 512L * 1024 * 1024;

    private const int MaxBatchFrames = 8;

    /// <summary>
    /// La carpeta de <paramref name="pickedPath"/> (un DNG de la secuencia o la carpeta misma), o <c>null</c> si no tiene
    /// ningún <c>.dng</c>. Los cuadros van en orden natural del nombre (<c>A_9.dng</c> antes que <c>A_10.dng</c>).
    /// </summary>
    public static CinemaDngFolder? Scan(string pickedPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(pickedPath);
        string folder = Directory.Exists(pickedPath) ? pickedPath : Path.GetDirectoryName(Path.GetFullPath(pickedPath))!;
        var frames = new DirectoryInfo(folder).EnumerateFiles("*.dng").Where(f => f.Length > 0).ToList();
        if (frames.Count == 0)
        {
            return null;
        }

        frames.Sort((a, b) => NaturalCompare(a.Name, b.Name));
        string[] wavs = Directory.GetFiles(folder, "*.wav");
        return new CinemaDngFolder(folder, [.. frames.Select(f => f.FullName)], frames.Max(f => f.Length), wavs.Length == 1 ? wavs[0] : null);
    }

    /// <summary>Compara nombres con los números por su valor.</summary>
    public static int NaturalCompare(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i]))
                {
                    i++;
                }

                while (j < b.Length && char.IsAsciiDigit(b[j]))
                {
                    j++;
                }

                string na = a[si..i].TrimStart('0'), nb = b[sj..j].TrimStart('0');
                int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (c != 0)
                {
                    return c;
                }

                continue;
            }

            int d = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
            if (d != 0)
            {
                return d;
            }

            i++;
            j++;
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }

    /// <summary>Cuadros por lote: los núcleos, sin pasar de <see cref="MaxBatchBytes"/> (un cuadro 4K son unos 90 MB).</summary>
    public static int BatchSize(long frameBytes, long fileBytes) =>
        (int)Math.Clamp(Math.Min(Math.Min(Environment.ProcessorCount, MaxBatchFrames), MaxBatchBytes / Math.Max(1, frameBytes + fileBytes)), 1, MaxBatchFrames);

    /// <summary>
    /// Revela <paramref name="sequence"/> y deja el intermedio en <paramref name="outputFolder"/>. Es un trabajo de Trabajos
    /// (avance por lote y cancelar); el archivo aparece entero o no aparece.
    /// </summary>
    /// <exception cref="ModuleException">El módulo falló (un DNG que no entiende, por ejemplo).</exception>
    /// <exception cref="IOException">No se pudo leer un DNG o escribir el intermedio.</exception>
    public static Task<CinemaDngResult> RunAsync(ModuleManager modules, ModuleDescriptor module, FfmpegExecutable ffmpeg, CinemaDngFolder sequence,
        CinemaDngOptions options, string outputFolder, string title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(options);
        return modules.RunAsync(module.Id, title, call => DevelopAsync(call, ffmpeg, sequence, options, outputFolder), cancellationToken);
    }

    private static async Task<CinemaDngResult> DevelopAsync(ModuleCall call, FfmpegExecutable ffmpeg, CinemaDngFolder sequence, CinemaDngOptions options,
        string outputFolder)
    {
        CancellationToken token = call.CancellationToken;
        call.Report(0, "Leyendo el primer fotograma");
        SharedMemoryRegion first = call.CreateRegion(sequence.LargestFrameBytes);
        SharedBufferRef firstFile = await ReadAsync(sequence.Frames[0], first, 0, sequence.LargestFrameBytes, token).ConfigureAwait(false);
        ModuleResult info = await call.InvokeAsync("info", null, [firstFile], 0, 0).ConfigureAwait(false);
        (int width, int height, int num, int den, string camera) = ReadInfo(info.Value);

        long frameBytes = (long)width * height * PixelFormats.BytesPerPixel(PixelFormats.Rgba16F);
        int batch = BatchSize(frameBytes, sequence.LargestFrameBytes);
        SharedMemoryRegion region = call.CreateRegion(batch * (sequence.LargestFrameBytes + frameBytes));

        Directory.CreateDirectory(outputFolder);
        string final = UniquePath(outputFolder, sequence.Name, ".mov");
        string temporary = final + ".rodaje-tmp";
        using JsonDocument parameters = Parameters(options);
        using Process encoder = StartEncoder(ffmpeg, width, height, num, den, sequence.Audio, temporary);
        Task<string> errors = encoder.StandardError.ReadToEndAsync(CancellationToken.None);
        bool done = false;
        try
        {
            Stream stdin = encoder.StandardInput.BaseStream;
            int total = sequence.Frames.Count;
            for (int start = 0; start < total; start += batch)
            {
                token.ThrowIfCancellationRequested();
                int count = Math.Min(batch, total - start);
                var pairs = new List<SharedBufferRef>(count * 2);
                long at = 0;
                for (int i = 0; i < count; i++)
                {
                    pairs.Add(await ReadAsync(sequence.Frames[start + i], region, at, sequence.LargestFrameBytes, token).ConfigureAwait(false));
                    at += sequence.LargestFrameBytes;
                    pairs.Add(region.Describe(at, frameBytes, new VideoLayout(width, height, width * 8, PixelFormats.Rgba16F)));
                    at += frameBytes;
                }

                double from = 0.98 * start / total, to = 0.98 * (start + count) / total;
                ModuleResult result = await call.InvokeAsync(Function, parameters.RootElement, pairs, from, to).ConfigureAwait(false);
                if (result.Buffers.Count != count)
                {
                    throw new ModuleProtocolException("«revelar» devolvió otra cantidad de fotogramas.");
                }

                try
                {
                    await Task.Run(() => Write(call, result.Buffers, frameBytes, stdin), token).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // La tubería se rompe cuando ffmpeg.exe muere: su motivo vale más que «canalización cerrada».
                    encoder.WaitForExit(5000);
                    throw new IOException($"ffmpeg.exe se detuvo: {Tail(await errors.ConfigureAwait(false))}");
                }
            }

            call.Report(0.98, "Guardando el video");
            stdin.Close();
            await encoder.WaitForExitAsync(token).ConfigureAwait(false);
            string stderr = await errors.ConfigureAwait(false);
            if (encoder.ExitCode != 0 || !File.Exists(temporary))
            {
                throw new IOException($"ffmpeg.exe terminó con código {encoder.ExitCode}: {Tail(stderr)}");
            }

            File.Move(temporary, final);
            done = true;
            return new CinemaDngResult(final, width, height, sequence.Frames.Count, num, den, camera);
        }
        finally
        {
            if (!done)
            {
                Kill(encoder);
                TryDelete(temporary);
            }
        }
    }

    private static void Write(ModuleCall call, IReadOnlyList<SharedBufferRef> frames, long frameBytes, Stream stdin)
    {
        foreach (SharedBufferRef frame in frames)
        {
            Span<byte> pixels = call.Map(frame);
            if (pixels.Length != frameBytes)
            {
                throw new ModuleProtocolException("«revelar» devolvió un fotograma de otro tamaño.");
            }

            stdin.Write(pixels);
        }
    }

    private static async Task<SharedBufferRef> ReadAsync(string path, SharedMemoryRegion region, long offset, long maxBytes, CancellationToken token)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        if (bytes.LongLength > maxBytes)
        {
            throw new IOException($"{Path.GetFileName(path)} cambió de tamaño mientras se importaba.");
        }

        bytes.CopyTo(region.GetSpan(offset, bytes.Length));
        return region.Describe(offset, bytes.Length);
    }

    /// <summary>La respuesta de <c>info</c>: tamaño, cadencia (24 si falta o no vale) y cámara.</summary>
    /// <exception cref="ModuleProtocolException">No trae un tamaño válido.</exception>
    internal static (int Width, int Height, int Num, int Den, string Camera) ReadInfo(JsonElement? value)
    {
        // TryGetInt32 lanza InvalidOperationException si el valor no es un número: se mira el tipo antes.
        if (value is not { ValueKind: JsonValueKind.Object } v
            || !v.TryGetProperty("ancho", out JsonElement w) || w.ValueKind != JsonValueKind.Number || !w.TryGetInt32(out int width) || width is < 16 or > 16_384
            || !v.TryGetProperty("alto", out JsonElement h) || h.ValueKind != JsonValueKind.Number || !h.TryGetInt32(out int height) || height is < 16 or > 16_384)
        {
            throw new ModuleProtocolException("«info» no devolvió un tamaño válido.");
        }

        (int num, int den) = v.TryGetProperty("cadencia", out JsonElement rate) && rate.ValueKind == JsonValueKind.Object
            && rate.TryGetProperty("num", out JsonElement n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out int rn)
            && rate.TryGetProperty("den", out JsonElement d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out int rd) && rn > 0 && rd > 0 && rn / (double)rd is >= 1 and <= 240
            ? (rn, rd)
            : (24, 1);
        string camera = $"{Text(v, "marca")} {Text(v, "modelo")}".Trim();
        return (width, height, num, den, camera.Length > 200 ? camera[..200] : camera);

        static string? Text(JsonElement o, string name) =>
            o.TryGetProperty(name, out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString()?.Trim() : null;
    }

    private static JsonDocument Parameters(CinemaDngOptions options) => JsonDocument.Parse(string.Create(CultureInfo.InvariantCulture,
        $$"""{"balance":"{{(options.Balance == "luz-dia" ? "luz-dia" : "camara")}}","exposicion":{{Math.Clamp(options.Exposure, -6, 6):0.###}},"espacio":"rec709","curva":"rec709","calidad":"{{(options.HighQuality ? "alta" : "rapida")}}"}"""));

    /// <summary>Argumentos de <c>ffmpeg.exe</c>: rgba16f por la entrada estándar → ProRes 422 HQ de 10 bits en MOV, Rec.709.</summary>
    public static IReadOnlyList<string> EncoderArguments(int width, int height, int num, int den, string? audio, string output)
    {
        List<string> args =
        [
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "rgbaf16le", "-s", $"{width}x{height}", "-framerate", $"{num}/{den}", "-i", "pipe:0",
        ];
        if (audio is not null)
        {
            args.AddRange(["-i", audio]);
        }

        args.AddRange(["-map", "0:v:0"]);
        if (audio is not null)
        {
            args.AddRange(["-map", "1:a:0?", "-c:a", "pcm_s24le", "-shortest"]);
        }

        args.AddRange(
        [
            "-vf", "scale=out_color_matrix=bt709:out_range=tv",
            "-c:v", "prores_ks", "-profile:v", "hq", "-pix_fmt", "yuv422p10le", "-vendor", "apl0",
            "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv",
            "-f", "mov", output,
        ]);
        return args;
    }

    private static Process StartEncoder(FfmpegExecutable ffmpeg, int width, int height, int num, int den, string? audio, string output)
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
        foreach (string argument in EncoderArguments(width, height, num, den, audio, output))
        {
            info.ArgumentList.Add(argument);
        }

        Process process = Process.Start(info) ?? throw new IOException("ffmpeg.exe no arrancó.");
        ChildProcessJob.TryAssign(process);
        return process;
    }

    private static string UniquePath(string folder, string name, string extension)
    {
        string safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        safe = safe.Length == 0 ? "CinemaDNG" : safe;
        string path = Path.Combine(folder, safe + extension);
        for (int i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(folder, $"{safe} ({i}){extension}");
        }

        return path;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
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

    private static string Tail(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[^300..];
    }
}
