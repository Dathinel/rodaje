using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Rodaje.Media.Proxies;

namespace Rodaje.Media.Export;

/// <summary>Lo que mide la primera pasada de <c>loudnorm</c> sobre el WAV de la mezcla.</summary>
/// <param name="IntegratedLufs">Sonoridad integrada (EBU R128).</param>
/// <param name="TruePeakDbtp">Pico verdadero.</param>
/// <param name="RangeLu">Rango de sonoridad.</param>
/// <param name="ThresholdLufs">Umbral relativo de la medición.</param>
/// <param name="TargetOffset">Ajuste fino que <c>loudnorm</c> calcula para la segunda pasada.</param>
public sealed record LoudnormMeasurement(double IntegratedLufs, double TruePeakDbtp, double RangeLu, double ThresholdLufs, double TargetOffset)
{
    /// <summary>Silencio (o casi): no hay nada que normalizar.</summary>
    public bool IsSilent => double.IsNegativeInfinity(IntegratedLufs) || IntegratedLufs < -70;
}

/// <summary>
/// Normalización de sonoridad en dos pasadas con el filtro <c>loudnorm</c> de FFmpeg: se mide el WAV y luego el filtro
/// aplica la ganancia en modo lineal (sin compresión dinámica si cabe en el pico pedido).
/// </summary>
public static class FfmpegLoudness
{
    /// <summary>Rango de sonoridad que se pide al filtro; con los valores medidos solo limita si hace falta.</summary>
    public const double DefaultRangeLu = 11;

    /// <summary>Mide el WAV para normalizarlo a <paramref name="targetLufs"/>.</summary>
    /// <exception cref="ExportException"><see cref="ExportErrorCode.EncoderFailed"/> si ffmpeg no pudo medir.</exception>
    public static LoudnormMeasurement Measure(FfmpegExecutable ffmpeg, string wavePath, double targetLufs, double maxTruePeakDbtp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        return Parse(RunForStderr(ffmpeg, MeasureArguments(wavePath, targetLufs, maxTruePeakDbtp), cancellationToken));
    }

    /// <summary>La línea de la primera pasada; el WAV va como <see cref="FfmpegPipeArguments.FilePath"/> por si su carpeta es larga.</summary>
    internal static IReadOnlyList<string> MeasureArguments(string wavePath, double targetLufs, double maxTruePeakDbtp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavePath);
        string filter = string.Create(CultureInfo.InvariantCulture,
            $"loudnorm=I={targetLufs:0.0#}:TP={maxTruePeakDbtp:0.0#}:LRA={DefaultRangeLu:0.0#}:print_format=json");
        return ["-hide_banner", "-nostdin", "-nostats", "-loglevel", "info", "-i", FfmpegPipeArguments.FilePath(wavePath), "-af", filter, "-f", "null", "NUL"];
    }

    /// <summary>El filtro de la segunda pasada, con lo medido.</summary>
    public static string Filter(double targetLufs, double maxTruePeakDbtp, LoudnormMeasurement measured)
    {
        ArgumentNullException.ThrowIfNull(measured);
        return string.Create(CultureInfo.InvariantCulture,
            $"loudnorm=I={targetLufs:0.0#}:TP={maxTruePeakDbtp:0.0#}:LRA={DefaultRangeLu:0.0#}:measured_I={measured.IntegratedLufs:0.00}:" +
            $"measured_TP={measured.TruePeakDbtp:0.00}:measured_LRA={measured.RangeLu:0.00}:measured_thresh={measured.ThresholdLufs:0.00}:" +
            $"offset={measured.TargetOffset:0.00}:linear=true");
    }

    /// <summary>Lee el bloque JSON que imprime <c>loudnorm</c> al final de la salida de error.</summary>
    internal static LoudnormMeasurement Parse(string stderr)
    {
        int start = stderr.LastIndexOf('{');
        int end = stderr.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new ExportException(ExportErrorCode.EncoderFailed, "ffmpeg no devolvió la medición de sonoridad.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(stderr.AsMemory(start, end - start + 1));
            JsonElement root = document.RootElement;
            return new LoudnormMeasurement(Number(root, "input_i"), Number(root, "input_tp"), Number(root, "input_lra"),
                Number(root, "input_thresh"), Number(root, "target_offset"));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException)
        {
            throw new ExportException(ExportErrorCode.EncoderFailed, "La medición de sonoridad de ffmpeg no se pudo leer.", ex);
        }
    }

    private static double Number(JsonElement root, string name)
    {
        string text = root.GetProperty(name).GetString() ?? "";
        return text switch
        {
            "-inf" => double.NegativeInfinity,
            "inf" => double.PositiveInfinity,
            _ => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
        };
    }

    private static string RunForStderr(FfmpegExecutable ffmpeg, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ffmpeg.EnsureUnchanged();
        var info = new ProcessStartInfo(ffmpeg.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(ffmpeg.Path)!,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        process.Start();
        ChildProcessJob.TryAssign(process);
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Ya salió o está saliendo: no hay nada que matar.
            }
        });
        Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        _ = output.GetAwaiter().GetResult();
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            (ExportErrorCode code, string message) = FfmpegMessages.Translate(stderr, process.ExitCode);
            throw new ExportException(code, "No se pudo medir la sonoridad. " + message);
        }

        return stderr;
    }
}
