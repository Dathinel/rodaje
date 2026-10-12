using System.Diagnostics;
using Rodaje.Media.Export;
using Rodaje.Media.Proxies;

namespace Rodaje.Export.Encoders;

/// <summary>Los codificadores que funcionan en este equipo, en orden de preferencia (hardware primero).</summary>
/// <remarks>El catálogo (<c>ExportEncoderAvailability</c>) entra por aquí.</remarks>
public interface IExportEncoderSource
{
    Task<IReadOnlyList<EncoderSpec>> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Sonda provisional: prueba cada candidato codificando unos cuadros (o unas muestras) con el <c>ffmpeg.exe</c>
/// verificado, con su primer formato de píxel, su primer perfil y sus argumentos fijos. Guarda el resultado.
/// </summary>
public sealed class ProbedEncoderSource : IExportEncoderSource
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly FfmpegExecutable _ffmpeg;
    private readonly IReadOnlyList<EncoderSpec> _candidates;
    private readonly Lock _gate = new();
    private Task<IReadOnlyList<EncoderSpec>>? _probe;

    public ProbedEncoderSource(FfmpegExecutable ffmpeg, IReadOnlyList<EncoderSpec> candidates)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(candidates);
        _ffmpeg = ffmpeg;
        _candidates = candidates;
    }

    /// <summary>Motivo por el que falló cada candidato descartado (tras <see cref="GetAsync"/>).</summary>
    public IReadOnlyDictionary<string, string> Rejected => _rejected;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _rejected = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<EncoderSpec>> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _probe ??= Task.Run(ProbeAll, CancellationToken.None);
            return _probe.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Argumentos de la prueba de un codificador; <c>-f null</c>, sin archivo.</summary>
    public static IReadOnlyList<string> ProbeArguments(EncoderSpec encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        List<string> args = ["-hide_banner", "-nostdin", "-loglevel", "error"];
        if (encoder.IsAudio)
        {
            int rate = encoder.SampleRates.Count == 0 || encoder.SampleRates.Contains(48_000) ? 48_000 : encoder.SampleRates[0];
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:sample_rate={rate}:duration=0.2", "-ac", "2", "-c:a", encoder.Id]);
        }
        else
        {
            args.AddRange(["-f", "lavfi", "-i", "testsrc2=s=640x360:r=25:d=0.2", "-c:v", encoder.Id]);
            if (encoder.PixelFormats.Count > 0)
            {
                args.AddRange(["-pix_fmt", encoder.PixelFormats[0].FfmpegName()]);
            }

            if (encoder.Profiles.FirstOrDefault(p => encoder.PixelFormats.Count == 0 || p.Allows(encoder.PixelFormats[0])) is { } profile)
            {
                args.AddRange(["-profile:v", profile.Id]);
            }
        }

        args.AddRange(encoder.BaseArguments);
        args.AddRange(["-f", "null", "-"]);
        return args;
    }

    private IReadOnlyList<EncoderSpec> ProbeAll()
    {
        var works = new bool[_candidates.Count];
        Parallel.For(0, _candidates.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            string? problem = Probe(_candidates[i]);
            works[i] = problem is null;
            if (problem is not null)
            {
                _rejected[_candidates[i].Id] = problem;
            }
        });
        return [.. _candidates.Where((_, i) => works[i])];
    }

    private string? Probe(EncoderSpec encoder)
    {
        var info = new ProcessStartInfo(_ffmpeg.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string argument in ProbeArguments(encoder))
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg.exe no arrancó.");
            Task<string> error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return $"{encoder.Id} no respondió en {Timeout.TotalSeconds:0} s.";
            }

            process.WaitForExit();
            return process.ExitCode == 0 ? null : $"{encoder.Id}: {Tail(error.Result)}";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return $"{encoder.Id}: {ex.Message}";
        }
    }

    private static string Tail(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[^300..];
    }
}
