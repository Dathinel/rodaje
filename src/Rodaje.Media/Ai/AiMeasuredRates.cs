using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rodaje.Media.Ai;

/// <summary>
/// Las velocidades reales de cada función en este equipo (<c>ia-tiempos.json</c> en la carpeta de datos): media móvil de
/// unidades por segundo por clave de <see cref="AiEstimator"/>. Si cambia el equipo (otra GPU, otro controlador, otros
/// hilos) se empieza de cero. Seguro entre hilos; nunca lanza al leer ni al guardar.
/// </summary>
public sealed class AiMeasuredRates : IAiMeasuredRates
{
    private const int MaxKeys = 64;
    private readonly object _lock = new();
    private readonly Dictionary<string, double> _rates = new(StringComparer.Ordinal);
    private readonly string? _path;
    private readonly string _machineKey;

    public AiMeasuredRates(string machineKey, string? path = null)
    {
        _machineKey = machineKey ?? "";
        _path = path;
        if (path is not null && TryRead(path) is { } entry && entry.Machine == _machineKey)
        {
            foreach ((string key, double rate) in entry.Rates.Take(MaxKeys))
            {
                if (rate > 0 && double.IsFinite(rate))
                {
                    _rates[key] = rate;
                }
            }
        }
    }

    public double? Rate(string key)
    {
        lock (_lock)
        {
            return _rates.TryGetValue(key, out double rate) ? rate : null;
        }
    }

    /// <summary>Anota una corrida: <paramref name="units"/> en <paramref name="seconds"/> (con la carga del modelo). Las muy cortas no cuentan.</summary>
    public void Record(string key, double units, double seconds)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!(units > 0) || !(seconds >= 1) || !double.IsFinite(units) || !double.IsFinite(seconds))
        {
            return;
        }

        double rate = units / seconds;
        Dictionary<string, double> snapshot;
        lock (_lock)
        {
            _rates[key] = _rates.TryGetValue(key, out double old) ? (old + rate) / 2 : rate;
            if (_rates.Count > MaxKeys)
            {
                _rates.Remove(_rates.Keys.First(k => k != key));
            }

            snapshot = new Dictionary<string, double>(_rates, StringComparer.Ordinal);
        }

        if (_path is not null)
        {
            TryWrite(_path, new AiRatesFile { Machine = _machineKey, Rates = snapshot });
        }
    }

    private static AiRatesFile? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), AiRatesJsonContext.Default.AiRatesFile) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void TryWrite(string path, AiRatesFile file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(file, AiRatesJsonContext.Default.AiRatesFile));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin el archivo, la próxima estimación sale de la tabla.
        }
    }
}

internal sealed class AiRatesFile
{
    public string Machine { get; set; } = "";

    public Dictionary<string, double> Rates { get; set; } = [];
}

[JsonSerializable(typeof(AiRatesFile))]
internal sealed partial class AiRatesJsonContext : JsonSerializerContext;

/// <summary>La RAM física del sistema (GlobalMemoryStatusEx).</summary>
public static partial class SystemMemory
{
    /// <summary>Total y disponible en bytes; (0, 0) si Windows no contesta.</summary>
    public static (long Total, long Available) Read()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? ((long)status.TotalPhys, (long)status.AvailPhys) : (0, 0);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
