using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Rodaje.TestMedia;

/// <summary>Falla de un proceso de ffmpeg, con los argumentos y el final de su salida de error.</summary>
public sealed class FfmpegException : Exception
{
    public FfmpegException()
    {
    }

    public FfmpegException(string message)
        : base(message)
    {
    }

    public FfmpegException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Recibe cada cuadro decodificado en gris (una muestra por píxel, sin relleno).</summary>
public delegate void GrayFrameHandler(int index, ReadOnlySpan<byte> gray);

/// <summary>
/// Un ffmpeg.exe externo, solo para desarrollo: genera y decodifica los medios de prueba.
/// Todos los argumentos van por <see cref="ProcessStartInfo.ArgumentList"/>, así que las rutas
/// con espacios y tildes no necesitan comillas.
/// </summary>
public sealed class FfmpegTool
{
    public const string EnvironmentVariable = "RODAJE_FFMPEG";

    private static readonly Lazy<(FfmpegTool? Tool, string? Reason)> DefaultInstance =
        new(() => Locate(Environment.GetEnvironmentVariable, AppContext.BaseDirectory));

    private static readonly string[] H264Preference = ["h264_nvenc", "h264_mf", "libopenh264", "libx264"];
    private static readonly string[] HevcPreference = ["hevc_nvenc", "hevc_mf", "libx265"];

    private readonly Lazy<string> _version;
    private readonly Lazy<IReadOnlySet<string>> _listedEncoders;
    private readonly ConcurrentDictionary<string, Lazy<string?>> _probes = new(StringComparer.Ordinal);

    public FfmpegTool(string executablePath, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ExecutablePath = Path.GetFullPath(executablePath);
        Source = source;
        _version = new Lazy<string>(ReadVersion);
        _listedEncoders = new Lazy<IReadOnlySet<string>>(ReadEncoders);
    }

    /// <summary>El ffmpeg encontrado según <see cref="Locate"/>, o null.</summary>
    public static FfmpegTool? Default => DefaultInstance.Value.Tool;

    /// <summary>Por qué no hay <see cref="Default"/>; null si lo hay.</summary>
    public static string? UnavailableReason => DefaultInstance.Value.Reason;

    public string ExecutablePath { get; }

    /// <summary>De dónde salió: la variable de entorno, el .entorno del repositorio o el PATH.</summary>
    public string Source { get; }

    /// <summary>Primera línea de <c>ffmpeg -version</c>.</summary>
    public string Version => _version.Value;

    /// <summary>Codificadores que el binario declara, aunque el hardware no los soporte.</summary>
    public IReadOnlySet<string> ListedEncoders => _listedEncoders.Value;

    /// <summary>ffprobe.exe junto a ffmpeg.exe, si existe.</summary>
    public string? FfprobePath
    {
        get
        {
            string candidate = Path.Combine(Path.GetDirectoryName(ExecutablePath)!, "ffprobe.exe");
            return File.Exists(candidate) ? candidate : null;
        }
    }

    public static IReadOnlyList<string> VideoEncoderCandidates(VideoCodec codec) =>
        codec == VideoCodec.H264 ? H264Preference : HevcPreference;

    /// <summary>
    /// Busca ffmpeg.exe: primero <c>RODAJE_FFMPEG</c> (archivo o carpeta), luego
    /// <c>.entorno\ffmpeg\bin</c> del repositorio (o de <c>RODAJE_ENTORNO</c>), y al final el PATH.
    /// </summary>
    internal static (FfmpegTool? Tool, string? Reason) Locate(Func<string, string?> environment, string startDirectory)
    {
        string? configured = environment(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string candidate = Directory.Exists(configured) ? Path.Combine(configured, "ffmpeg.exe") : configured;
            return File.Exists(candidate)
                ? (new FfmpegTool(candidate, EnvironmentVariable), null)
                : (null, $"{EnvironmentVariable} apunta a \"{configured}\", pero ahí no hay ffmpeg.exe.");
        }

        foreach (string candidate in EntornoCandidates(environment, startDirectory))
        {
            if (File.Exists(candidate))
            {
                return (new FfmpegTool(candidate, ".entorno"), null);
            }
        }

        foreach (string directory in (environment("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim().Trim('"'), "ffmpeg.exe");
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return (new FfmpegTool(candidate, "PATH"), null);
            }
        }

        return (null, $"No se encontró ffmpeg.exe: define {EnvironmentVariable}, coloca ffmpeg en .entorno\\ffmpeg\\bin o agrégalo al PATH.");
    }

    /// <summary>Si el codificador está declarado y además codifica de verdad unos cuadros (o muestras).</summary>
    public bool IsEncoderUsable(string encoder) => ProbeEncoder(encoder) is null;

    /// <summary>null si el codificador funciona; si no, el motivo.</summary>
    public string? ProbeEncoder(string encoder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoder);
        return _probes.GetOrAdd(encoder, e => new Lazy<string?>(() => RunProbe(e))).Value;
    }

    /// <summary>El primer codificador del códec que funciona, en orden NVENC, Media Foundation, OpenH264 y x264/x265.</summary>
    public string? FindVideoEncoder(VideoCodec codec) =>
        VideoEncoderCandidates(codec).FirstOrDefault(IsEncoderUsable);

    public static string AudioEncoderName(AudioCodec codec) => codec switch
    {
        AudioCodec.Aac => "aac",
        AudioCodec.Opus => "libopus",
        _ => throw new ArgumentOutOfRangeException(nameof(codec), $"{codec} no tiene codificador."),
    };

    /// <summary>
    /// Ejecuta ffmpeg. <paramref name="writeInput"/> escribe en su entrada estándar y
    /// <paramref name="readOutput"/> lee su salida estándar; cualquiera puede ser null.
    /// </summary>
    public void Run(IEnumerable<string> arguments, Action<Stream>? writeInput = null, Action<Stream>? readOutput = null) =>
        RunExecutable(ExecutablePath, arguments, writeInput, readOutput);

    /// <summary>Decodifica el primer video a gris, cuadro a cuadro y sin duplicar ni descartar cuadros.</summary>
    public int DecodeGrayFrames(string path, int width, int height, GrayFrameHandler onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        int count = 0;
        Run(
            [
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-i", path,
                "-map", "0:v:0", "-fps_mode", "passthrough",
                "-f", "rawvideo", "-pix_fmt", "gray", "-s", Invariant($"{width}x{height}"), "-",
            ],
            readOutput: output =>
            {
                var buffer = new byte[width * height];
                while (true)
                {
                    int read = output.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                    if (read < buffer.Length)
                    {
                        if (read > 0)
                        {
                            throw new FfmpegException($"El cuadro {count} llegó incompleto ({read} de {buffer.Length} bytes).");
                        }

                        return;
                    }

                    onFrame(count++, buffer);
                }
            });
        return count;
    }

    /// <summary>Decodifica el primer audio a float entrelazado, remuestreado si hace falta.</summary>
    public float[] DecodeAudio(string path, int sampleRate = MediaSpec.AudioSampleRate, int channels = MediaSpec.AudioChannels)
    {
        float[] samples = [];
        Run(
            [
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-i", path,
                "-map", "0:a:0", "-ac", channels.ToString(CultureInfo.InvariantCulture),
                "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
                "-f", "f32le", "-",
            ],
            readOutput: output =>
            {
                using var memory = new MemoryStream();
                output.CopyTo(memory);
                samples = new float[memory.Length / sizeof(float)];
                Buffer.BlockCopy(memory.GetBuffer(), 0, samples, 0, samples.Length * sizeof(float));
            });
        return samples;
    }

    /// <summary>
    /// Cuántos cuadros I, P y B tiene el primer video según ffprobe (los primeros
    /// <paramref name="maxFrames"/>, si se indica; ffprobe es lento con archivos largos); null si no hay ffprobe.
    /// </summary>
    public IReadOnlyDictionary<char, int>? CountPictureTypes(string path, int? maxFrames = null)
    {
        string? ffprobe = FfprobePath;
        if (ffprobe is null)
        {
            return null;
        }

        var counts = new Dictionary<char, int>();
        RunExecutable(
            ffprobe,
            [
                "-v", "error", "-select_streams", "v:0",
                .. maxFrames is { } limit ? ["-read_intervals", Invariant($"%+#{limit}")] : Array.Empty<string>(),
                "-show_entries", "frame=pict_type", "-of", "csv=p=0", path,
            ],
            null,
            output =>
            {
                using var reader = new StreamReader(output, Encoding.ASCII);
                while (reader.ReadLine() is { } line)
                {
                    line = line.Trim().TrimEnd(',');
                    if (line.Length > 0)
                    {
                        counts[line[0]] = counts.GetValueOrDefault(line[0]) + 1;
                    }
                }
            });
        return counts;
    }

    internal static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    private static IEnumerable<string> EntornoCandidates(Func<string, string?> environment, string startDirectory)
    {
        string? entorno = environment("RODAJE_ENTORNO");
        if (!string.IsNullOrWhiteSpace(entorno))
        {
            yield return Path.Combine(entorno, "ffmpeg", "bin", "ffmpeg.exe");
        }

        // Sube por las carpetas: desde un worktree en .claude\worktrees también encuentra el repositorio principal.
        for (DirectoryInfo? dir = new(startDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
            {
                yield return Path.Combine(dir.FullName, ".entorno", "ffmpeg", "bin", "ffmpeg.exe");
            }
        }
    }

    private string? RunProbe(string encoder)
    {
        if (!ListedEncoders.Contains(encoder))
        {
            return $"{Path.GetFileName(ExecutablePath)} ({Source}) no incluye el codificador {encoder}.";
        }

        bool audio = encoder is "aac" or "libopus" or "opus" or "aac_mf";
        string[] input = audio
            ? ["-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000:duration=0.2"]
            : ["-f", "lavfi", "-i", "color=c=gray:s=320x240:r=25:d=0.4"];
        try
        {
            Run(["-hide_banner", "-loglevel", "error", "-nostdin", .. input, "-c:" + (audio ? "a" : "v"), encoder, "-f", "null", "-"]);
            return null;
        }
        catch (FfmpegException ex)
        {
            return $"El codificador {encoder} está en ffmpeg pero no funciona en este equipo: {ex.Message}";
        }
    }

    private string ReadVersion()
    {
        string version = string.Empty;
        Run(["-hide_banner", "-version"], readOutput: output =>
        {
            using var reader = new StreamReader(output);
            version = reader.ReadLine() ?? string.Empty;
            reader.ReadToEnd();
        });
        return version;
    }

    private HashSet<string> ReadEncoders()
    {
        var encoders = new HashSet<string>(StringComparer.Ordinal);
        Run(["-hide_banner", "-encoders"], readOutput: output =>
        {
            using var reader = new StreamReader(output);
            bool table = false;
            while (reader.ReadLine() is { } line)
            {
                if (!table)
                {
                    table = line.TrimStart().StartsWith("------", StringComparison.Ordinal);
                    continue;
                }

                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    encoders.Add(parts[1]);
                }
            }
        });
        return encoders;
    }

    private static void RunExecutable(string executable, IEnumerable<string> arguments, Action<Stream>? writeInput, Action<Stream>? readOutput)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = writeInput is not null,
            RedirectStandardOutput = readOutput is not null,
            RedirectStandardError = true,
        };
        List<string> argumentList = [.. arguments];
        foreach (string argument in argumentList)
        {
            info.ArgumentList.Add(argument);
        }

        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr)
                {
                    if (stderr.Length < 16_384)
                    {
                        stderr.AppendLine(e.Data);
                    }
                }
            }
        };

        process.Start();
        process.BeginErrorReadLine();

        Exception? pipeFailure = null;
        Task writer = Task.CompletedTask;
        if (writeInput is not null)
        {
            writer = Task.Run(() =>
            {
                try
                {
                    using Stream input = process.StandardInput.BaseStream;
                    writeInput(input);
                }
                catch (IOException ex)
                {
                    // ffmpeg cerró la tubería: el motivo real queda en su salida de error.
                    pipeFailure = ex;
                }
            });
        }

        try
        {
            if (readOutput is not null)
            {
                Stream stdout = process.StandardOutput.BaseStream;
                readOutput(stdout);
                if (stdout.CanRead)
                {
                    stdout.CopyTo(Stream.Null);
                }
            }

            writer.GetAwaiter().GetResult();
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        process.WaitForExit();
        if (process.ExitCode != 0 || pipeFailure is not null)
        {
            string log;
            lock (stderr)
            {
                log = stderr.ToString().Trim();
            }

            string message = Invariant(
                $"{Path.GetFileName(executable)} terminó con código {process.ExitCode}.\nArgumentos: {string.Join(' ', argumentList)}\n{log}");
            throw pipeFailure is null ? new FfmpegException(message) : new FfmpegException(message, pipeFailure);
        }
    }
}
