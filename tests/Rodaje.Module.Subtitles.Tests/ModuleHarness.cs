using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Rodaje.Modules.Protocol;

namespace Rodaje.Module.Subtitles.Tests;

/// <summary>Necesita el modelo en <c>.entorno\modelos</c> (y el corpus si <paramref name="corpus"/>); con <c>LongRunning</c>, además <c>RODAJE_PRUEBAS_LARGAS=1</c>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ModelFactAttribute : FactAttribute
{
    private bool _long;
    private bool _corpus;

    public ModelFactAttribute() => Skip = Reason(false, false);

    public bool Corpus
    {
        get => _corpus;
        set => (_corpus, Skip) = (value, Reason(_long, value));
    }

    public bool LongRunning
    {
        get => _long;
        set => (_long, Skip) = (value, Reason(value, _corpus));
    }

    private static string? Reason(bool longRunning, bool corpus) =>
        Tests.Corpus.ModelPath is null ? "Omitida: sin el modelo; ejecuta build\\preparar-modelos.ps1."
        : corpus && Tests.Corpus.FleursDirectory is null ? "Omitida: sin el corpus; ejecuta build\\preparar-modelos.ps1 -Corpus."
        : longRunning && Environment.GetEnvironmentVariable("RODAJE_PRUEBAS_LARGAS") != "1" ? "Omitida: prueba larga (RODAJE_PRUEBAS_LARGAS=1)."
        : null;
}

/// <summary>El módulo de subtítulos como proceso de verdad, con el audio en memoria compartida.</summary>
internal static class ModuleHarness
{
    public static string Exe =>
        Environment.GetEnvironmentVariable("RODAJE_MODULO_SUBTITULOS") is { Length: > 0 } exe ? exe : Path.Combine(AppContext.BaseDirectory, "Rodaje.Module.Subtitles.exe");

    public static Task<ModuleProcess> StartAsync(ModuleClientOptions? options = null) => ModuleProcess.StartAsync(Exe, options: options);

    /// <summary>Copia el audio a una región nueva y devuelve su búfer.</summary>
    public static SharedBufferRef Upload(ModuleClient client, float[] samples)
    {
        long bytes = samples.Length * 4L;
        SharedMemoryRegion region = client.CreateRegion(bytes);
        MemoryMarshal.AsBytes(samples.AsSpan()).CopyTo(region.GetSpan(0, bytes));
        return region.Describe(0, bytes, audio: new AudioLayout(Corpus.Rate, 1, samples.Length));
    }

    public static JsonElement Parameters(string device = "auto", string language = "es")
    {
        string json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["model"] = Corpus.ModelPath!,
            ["device"] = device,
            ["language"] = language,
        });
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static Transcript Parse(ModuleResult result) =>
        result.Value!.Value.Deserialize(TranscriptJson.Default.Transcript) ?? throw new InvalidDataException("resultado vacío");
}

/// <summary>Muestrea la memoria dedicada de GPU de un proceso con el contador de Windows «GPU Process Memory».</summary>
internal sealed class VramSampler : IAsyncDisposable
{
    private readonly Process _sampler;
    private readonly List<double> _mb = [];
    private readonly Task _reader;

    public VramSampler(int pid)
    {
        string script = $"while ($true) {{ try {{ $s = (Get-Counter '\\GPU Process Memory(pid_{pid}_*)\\Dedicated Usage' -ErrorAction Stop).CounterSamples; " +
            "[Console]::WriteLine((($s | Measure-Object CookedValue -Sum).Sum / 1MB).ToString([Globalization.CultureInfo]::InvariantCulture)) } catch { [Console]::WriteLine('0') }; Start-Sleep -Milliseconds 300 }";
        _sampler = Process.Start(new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        _reader = Task.Run(async () =>
        {
            while (await _sampler.StandardOutput.ReadLineAsync() is { } line)
            {
                if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out double mb))
                {
                    lock (_mb)
                    {
                        _mb.Add(mb);
                    }
                }
            }
        });
    }

    public double PeakMb
    {
        get
        {
            lock (_mb)
            {
                return _mb.DefaultIfEmpty(0).Max();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _sampler.Kill(true);
        }
        catch (InvalidOperationException)
        {
        }

        await _reader.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _sampler.Dispose();
    }
}
