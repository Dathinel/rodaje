using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Rodaje.Core.Timing;
using Rodaje.Media.Export;
using Rodaje.Media.Proxies;
using Xunit.Sdk;

namespace Rodaje.Export.Tests.Cpu;

/// <summary>
/// Lo único que necesita una exportación por CPU: el <c>ffmpeg.exe</c> LGPL verificado y su <c>ffprobe.exe</c> junto a las
/// DLL. A diferencia de <see cref="Rodaje.TestMedia.Skip.Proxies"/>, no pide un codificador de HEVC ni de proxies, así que
/// estas pruebas corren en el runner de Windows sin GPU de la CI.
/// </summary>
internal static class CpuExportEnvironment
{
    private static readonly Lazy<(FfmpegExecutable? Executable, string? Reason)> State = new(() =>
    {
        if (Rodaje.TestMedia.Skip.FfmpegLibraries is { } libraries)
        {
            return (null, libraries);
        }

        try
        {
            FfmpegExecutable? executable = FfmpegExecutable.TryLocate(out string? failure);
            return executable is null ? (null, "Omitida: " + failure) : (executable, null);
        }
        catch (DllNotFoundException ex)
        {
            // Fuera de Windows (FFmpeg de Linux enlazado como .dll): ffmpeg.exe va dentro de un trabajo de Windows.
            return (null, "Omitida: lanzar ffmpeg.exe necesita los trabajos de Windows (ChildProcessJob): " + ex.Message);
        }
    });

    private static readonly Lazy<(HashSet<string>? Encoders, string? Failure)> Listed = new(() =>
    {
        if (State.Value.Executable is not { } ffmpeg)
        {
            return (null, null);
        }

        try
        {
            string output = Encoding.UTF8.GetString(RunBinary(ffmpeg.Path, ["-hide_banner", "-nostdin", "-encoders"], TimeSpan.FromSeconds(30)));
            HashSet<string> encoders = FfmpegExportEncoderProbe.ParseEncoderList(output);
            return encoders.Count > 0 ? (encoders, null) : (null, "ffmpeg -encoders no listó ningún codificador.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return (null, ex.Message);
        }
    });

    /// <summary>Motivo para omitir, o <c>null</c> si hay <c>ffmpeg.exe</c> y <c>ffprobe.exe</c> LGPL.</summary>
    public static string? Reason => State.Value.Reason;

    public static FfmpegExecutable Ffmpeg => State.Value.Executable
        ?? throw new InvalidOperationException(Reason ?? "Sin ffmpeg.exe.");

    public static string FfprobePath => Path.Combine(Path.GetDirectoryName(Ffmpeg.Path)!, "ffprobe.exe");

    /// <summary>
    /// <see cref="Reason"/> y además que este <c>ffmpeg.exe</c> traiga compilados los codificadores. Solo omite si falta
    /// alguno en <c>-encoders</c> (causa de compilación); si está y falla al codificar, la prueba corre y falla con su error.
    /// </summary>
    public static string? Encoders(params string?[] ids)
    {
        if (Reason is { } reason)
        {
            return reason;
        }

        // Si no se pudo leer la lista no se omite: la prueba correrá y dirá qué pasa.
        if (Listed.Value.Encoders is not { } encoders)
        {
            return null;
        }

        string? missing = ids.FirstOrDefault(id => id is not null && !encoders.Contains(id));
        return missing is null ? null : $"Omitida: este ffmpeg.exe ({Ffmpeg.Path}) no trae el codificador {missing}.";
    }

    /// <summary>Ejecuta el <c>ffmpeg.exe</c> (o <c>ffprobe.exe</c>) y devuelve la salida estándar en bytes.</summary>
    public static byte[] RunBinary(string executable, IEnumerable<string> arguments, TimeSpan? timeout = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"No arrancó {executable}.");
        // En el trabajo de Windows de Rodaje.Media: si el proceso de pruebas muere, Windows cierra también este ffmpeg.exe.
        ChildProcessJob.TryAssign(process);
        process.StandardInput.Close();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        if (!process.WaitForExit(timeout ?? TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{Path.GetFileName(executable)} {string.Join(' ', info.ArgumentList)} no terminó a tiempo.");
        }

        process.WaitForExit();
        copy.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} {string.Join(' ', info.ArgumentList)} terminó con código {process.ExitCode}: {error.GetAwaiter().GetResult()}");
        }

        return output.ToArray();
    }

    /// <summary>
    /// WAV float estéreo a 48 kHz con un tono de 440 Hz (amplitud 1/8, como la fuente <c>sine</c> de lavfi), escrito aquí
    /// mismo: la entrada de la prueba no lanza otro <c>ffmpeg.exe</c> que pueda morir por su cuenta (tarea 330).
    /// </summary>
    public static string SineWav(string path, double seconds)
    {
        const int rate = 48_000;
        const int channels = 2;
        int samples = checked((int)Math.Round(seconds * rate));
        int dataBytes = checked(samples * channels * sizeof(float));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)3); // WAVE_FORMAT_IEEE_FLOAT
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(rate * channels * sizeof(float));
        writer.Write((short)(channels * sizeof(float)));
        writer.Write((short)32);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (int i = 0; i < samples; i++)
        {
            float value = (float)(Math.Sin(2 * Math.PI * 440 * i / rate) / 8);
            writer.Write(value);
            writer.Write(value);
        }

        return path;
    }

    public static ExportVideoFormat Format(int width, int height, ExportPixelFormat pixels) =>
        new(width, height, Rational.Fps30, pixels, ExportColor.For(width, height, pixels));
}

/// <summary>Carpeta temporal de una prueba (con espacios y «ñ» en la ruta), que se borra al terminar.</summary>
internal sealed class ExportScratch : IDisposable
{
    public ExportScratch()
    {
        Directory = Path.Combine(Path.GetTempPath(), "rodaje-pruebas-export", "carpeta con espacios ñ", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    public string Combine(string name) => Path.Combine(Directory, name);

    /// <summary>Archivos que quedan en la carpeta (para comprobar que no quedan temporales).</summary>
    public string[] Leftovers(params string[] except) =>
        [.. System.IO.Directory.EnumerateFileSystemEntries(Directory).Where(p => !except.Contains(p, StringComparer.OrdinalIgnoreCase))];

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>El proceso <c>ffmpeg.exe</c> de un <see cref="FfmpegPipeSink"/>.</summary>
internal static class SinkRuns
{
    /// <summary>Id del <c>ffmpeg.exe</c> en marcha, o <c>null</c> si no hay.</summary>
    public static int? ProcessId(FfmpegPipeSink sink) => sink.ProcessId;

    public static bool IsAlive(int? processId)
    {
        if (processId is not { } id)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// Cuadros en el formato pedido, de una fuente lavfi (<c>testsrc2</c> por defecto) generados una vez por <c>ffmpeg.exe</c>
/// o, con <see cref="Synthetic"/>, hechos aquí mismo; se guardan con un pitch más ancho que la fila para que el sink tenga
/// que quitarlo.
/// </summary>
internal sealed class LavfiFrames
{
    private readonly byte[][] _padded;
    private readonly byte[][] _packed;
    private readonly int[] _offsets = new int[4];
    private readonly int[] _pitches = new int[4];

    public LavfiFrames(ExportVideoFormat format, int distinct, string source = "testsrc2", int padding = 96)
        : this(format, distinct, Lavfi(format, distinct, source), padding)
    {
    }

    private LavfiFrames(ExportVideoFormat format, int distinct, byte[] raw, int padding)
    {
        Format = format;
        ExportPixelFormat pixels = format.PixelFormat;
        int frameBytes = checked((int)pixels.FrameBytes(format.Width, format.Height));
        if (raw.Length != (long)frameBytes * distinct)
        {
            throw new InvalidOperationException($"La fuente dio {raw.Length} bytes y se esperaban {(long)frameBytes * distinct}.");
        }

        int offset = 0;
        for (int plane = 0; plane < pixels.PlaneCount(); plane++)
        {
            _offsets[plane] = offset;
            _pitches[plane] = pixels.RowBytes(plane, format.Width, format.Height) + padding;
            offset += _pitches[plane] * pixels.PlaneSize(plane, format.Width, format.Height).Height;
        }

        _padded = new byte[distinct][];
        _packed = new byte[distinct][];
        for (int i = 0; i < distinct; i++)
        {
            _packed[i] = raw.AsSpan(i * frameBytes, frameBytes).ToArray();
            _padded[i] = new byte[offset];
            Array.Fill(_padded[i], (byte)0xA5);
            int read = 0;
            for (int plane = 0; plane < pixels.PlaneCount(); plane++)
            {
                int rowBytes = pixels.RowBytes(plane, format.Width, format.Height);
                int rows = pixels.PlaneSize(plane, format.Width, format.Height).Height;
                for (int y = 0; y < rows; y++)
                {
                    _packed[i].AsSpan(read, rowBytes).CopyTo(_padded[i].AsSpan(_offsets[plane] + (y * _pitches[plane])));
                    read += rowBytes;
                }
            }
        }
    }

    /// <summary>
    /// Cuadros distintos hechos aquí mismo, sin lanzar <c>ffmpeg.exe</c>: un degradado por plano que se corre con cada cuadro,
    /// con las muestras de 10 bits dentro de su rango. Para las pruebas que no comparan con una imagen de referencia.
    /// </summary>
    public static LavfiFrames Synthetic(ExportVideoFormat format, int distinct, int padding = 96)
    {
        ExportPixelFormat pixels = format.PixelFormat;
        int frameBytes = checked((int)pixels.FrameBytes(format.Width, format.Height));
        int sampleBytes = pixels.BytesPerSample();
        int bits = pixels.BitDepth();
        var raw = new byte[(long)frameBytes * distinct];
        int at = 0;
        for (int frame = 0; frame < distinct; frame++)
        {
            for (int plane = 0; plane < pixels.PlaneCount(); plane++)
            {
                int samplesPerRow = pixels.RowBytes(plane, format.Width, format.Height) / sampleBytes;
                int rows = pixels.PlaneSize(plane, format.Width, format.Height).Height;
                for (int y = 0; y < rows; y++)
                {
                    for (int x = 0; x < samplesPerRow; x++)
                    {
                        int value = (x + (2 * y) + (7 * frame) + (50 * plane)) % (1 << Math.Min(bits, 8));
                        if (sampleBytes == 1)
                        {
                            raw[at++] = (byte)value;
                        }
                        else
                        {
                            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(at), (ushort)(value << (bits - 8)));
                            at += 2;
                        }
                    }
                }
            }
        }

        return new LavfiFrames(format, distinct, raw, padding);
    }

    private static byte[] Lavfi(ExportVideoFormat format, int distinct, string source)
    {
        string size = string.Create(CultureInfo.InvariantCulture, $"{format.Width}x{format.Height}");
        return CpuExportEnvironment.RunBinary(CpuExportEnvironment.Ffmpeg.Path,
        [
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-f", "lavfi", "-i", $"{source}=size={size}:rate=30",
            "-frames:v", distinct.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", format.PixelFormat.FfmpegName(), "-f", "rawvideo", "pipe:1",
        ]);
    }

    public ExportVideoFormat Format { get; }

    public int Distinct => _packed.Length;

    /// <summary>El cuadro <paramref name="index"/> de la salida; repite los distintos en ciclo.</summary>
    public ExportVideoFrame Frame(long index)
    {
        byte[] data = _padded[index % _padded.Length];
        int count = Format.PixelFormat.PlaneCount();
        ReadOnlySpan<byte> Plane(int p) => p < count ? data.AsSpan(_offsets[p], (p + 1 < count ? _offsets[p + 1] : data.Length) - _offsets[p]) : default;
        return new ExportVideoFrame(index, Format, Plane(0), _pitches[0], Plane(1), _pitches[1], Plane(2), _pitches[2], Plane(3), _pitches[3]);
    }

    /// <summary>Los bytes del cuadro sin pitch, tal como ffmpeg los dio (para comparar lo decodificado).</summary>
    public ReadOnlySpan<byte> Packed(long index) => _packed[index % _packed.Length];

    /// <summary>Escribe todos los cuadros de la petición y cierra; devuelve el resultado y el tiempo.</summary>
    public (ExportSinkResult Result, TimeSpan Elapsed) Run(FfmpegPipeSink sink, ExportSinkRequest request, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        sink.Start(request, cancellationToken);
        for (long i = 0; i < request.FrameCount; i++)
        {
            sink.WriteVideo(Frame(i), cancellationToken);
        }

        ExportSinkResult result = sink.Complete(cancellationToken);
        return (result, clock.Elapsed);
    }
}

/// <summary>Hecho que solo necesita el <c>ffmpeg.exe</c> LGPL (y, si se nombran, esos codificadores compilados).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class CpuExportFactAttribute : FactAttribute
{
    public CpuExportFactAttribute(params string[] encoders) => Skip = CpuExportEnvironment.Encoders(encoders);
}

/// <summary>Teoría que solo necesita el <c>ffmpeg.exe</c> LGPL; cada fila puede pedir sus codificadores con su propio atributo.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class CpuExportTheoryAttribute : TheoryAttribute
{
    public CpuExportTheoryAttribute() => Skip = CpuExportEnvironment.Reason;
}

/// <summary>
/// Una fila de exportación por CPU (codificador de video, contenedor, píxel y codificador de audio), que se omite con su
/// motivo si este <c>ffmpeg.exe</c> no trae compilado alguno de sus codificadores.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class CpuExportCaseAttribute : DataAttribute
{
    private readonly object[] _row;

    public CpuExportCaseAttribute(string video, ExportContainer container, ExportPixelFormat pixels, string audio)
    {
        _row = [video, container, pixels, audio];
        Skip = CpuExportEnvironment.Encoders(video, audio);
    }

    public override IEnumerable<object[]> GetData(MethodInfo testMethod) => [_row];
}

/// <summary>Una fila de exportación solo de audio (contenedor y codificador), omitida si el codificador no está compilado.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class CpuAudioExportCaseAttribute : DataAttribute
{
    private readonly object[] _row;

    public CpuAudioExportCaseAttribute(ExportContainer container, string audio)
    {
        _row = [container, audio];
        Skip = CpuExportEnvironment.Encoders(audio);
    }

    public override IEnumerable<object[]> GetData(MethodInfo testMethod) => [_row];
}
