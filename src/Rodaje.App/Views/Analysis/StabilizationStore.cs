using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Rodaje.Analysis.Video;
using Rodaje.App.Services;
using Rodaje.Playback.Composition;

namespace Rodaje.App.Views.Analysis;

/// <summary>
/// Los análisis de movimiento de «Estabilizar» en la caché (<c>cache\estabilizar\&lt;clave&gt;.rmot</c>) y sus correcciones
/// ya calculadas por intensidad y recorte, en memoria. El motor los pide al armar el plan (<c>FindStabilization</c>).
/// </summary>
internal sealed class StabilizationStore
{
    private const int MaxCorrections = 16;

    private readonly ConcurrentDictionary<string, MotionTrack?> _tracks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string, double, bool), StabilizationTrack?> _corrections = new();

    public StabilizationStore(string folder) => Folder = folder;

    public static StabilizationStore Default { get; } = new(Path.Combine(AppDataPaths.Root, "cache", "estabilizar"));

    public string Folder { get; }

    /// <summary>Clave estable de un tramo de un medio: la huella del archivo y los cuadros analizados.</summary>
    public static string KeyFor(string fingerprint, long firstFrame, long frameCount)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"rmot1|{fingerprint}|{firstFrame}|{frameCount}"));
        return Convert.ToHexStringLower(hash.AsSpan(0, 12));
    }

    public string PathOf(string key) => Path.Combine(Folder, key + ".rmot");

    public MotionTrack? Load(string key) => _tracks.GetOrAdd(key, k =>
    {
        if (!IsKey(k))
        {
            return null;
        }

        try
        {
            string path = PathOf(k);
            return File.Exists(path) ? MotionTrack.FromBytes(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    });

    /// <summary>Guarda el análisis (temporal y renombrar: un apagón no deja uno cortado) y lo deja en memoria.</summary>
    public void Save(string key, MotionTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        Directory.CreateDirectory(Folder);
        string path = PathOf(key);
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(track.ToBytes());
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
        _tracks[key] = track;
        foreach ((string, double, bool) stale in _corrections.Keys.Where(k => k.Item1 == key))
        {
            _corrections.TryRemove(stale, out _);
        }
    }

    /// <summary>Correcciones para el motor; <c>null</c> si el análisis no está.</summary>
    public StabilizationTrack? Find(string key, double intensity, bool autoCrop)
    {
        var id = (key, Math.Round(intensity, 4), autoCrop);
        if (_corrections.TryGetValue(id, out StabilizationTrack? cached))
        {
            return cached;
        }

        if (Load(key) is not { } track)
        {
            return null;
        }

        StabilizationResult result = track.Stabilize(new StabilizeOptions(intensity, autoCrop));
        var corrections = new StabilizationTrack(track.FirstFrame, result.Corrections.Select(c => new LayerStabilization(c.A, c.B, c.X, c.Y)));
        if (_corrections.Count >= MaxCorrections)
        {
            _corrections.Clear();
        }

        _corrections[id] = corrections;
        return corrections;
    }

    private static bool IsKey(string key) => key.Length is > 0 and <= 64 && key.All(char.IsAsciiHexDigitLower);
}
