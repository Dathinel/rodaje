using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;

namespace Rodaje.Media.RenderCache;

/// <summary>
/// Lo que dice el <c>.json</c> de un fragmento: hasta 2 s de cuadros seguidos de una secuencia, todo intra y sin audio,
/// con la huella de cada cuadro en orden (D-060).
/// </summary>
/// <param name="Width">Ancho de la secuencia en píxeles.</param>
/// <param name="Height">Alto de la secuencia en píxeles.</param>
/// <param name="FrameRate">Tasa de la secuencia.</param>
/// <param name="ColorSpace">Espacio de color de la secuencia (SDR, PQ o HLG).</param>
/// <param name="Codec">Códec del <c>.mov</c>, por ejemplo <c>prores_hq</c> o <c>hevc_intra</c>.</param>
/// <param name="PlaybackOnly">El fragmento solo sirve para reproducir (HEVC por la GPU): la exportación no lo reutiliza.</param>
/// <param name="Frames">La huella de cada cuadro del fragmento, del primero al último.</param>
/// <param name="MovieBytes">Tamaño del <c>.mov</c>; si el archivo no lo tiene, el fragmento no vale.</param>
public sealed record RenderCacheFragmentMetadata(int Width, int Height, Rational FrameRate, SequenceColorSpace ColorSpace, string Codec,
    bool PlaybackOnly, IReadOnlyList<RenderCacheKey> Frames, long MovieBytes);

/// <summary>Un fragmento en el almacén: su <c>.mov</c> y lo que dice su <c>.json</c>.</summary>
public sealed record RenderCacheFragment(string MoviePath, string MetadataPath, RenderCacheFragmentMetadata Metadata);

/// <summary>Dónde está el cuadro de una huella: el fragmento y su índice dentro de él.</summary>
public readonly record struct RenderCacheHit(RenderCacheFragment Fragment, int FrameIndex);

/// <summary>Opciones de <see cref="IRenderCacheStore.Cleanup"/>.</summary>
public sealed record RenderCacheCleanupOptions
{
    /// <summary>Los temporales más viejos que esto se borran (un cálculo cortado por un apagón).</summary>
    public TimeSpan TemporaryMaxAge { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Tope del almacén; se borran los fragmentos usados hace más tiempo hasta quedar por debajo. Nulo, sin tope.</summary>
    public long? MaxTotalBytes { get; init; }

    /// <summary>Huellas de las recetas vigentes: un fragmento con alguna de ellas no se borra aunque se pase el tope.</summary>
    public IReadOnlySet<RenderCacheKey>? ProtectedKeys { get; init; }
}

/// <summary>Lo que hizo una limpieza.</summary>
public sealed record RenderCacheCleanupReport(int TemporaryFiles, int Orphans, int Evicted, long BytesFreed, int Protected, long RemainingBytes);

/// <summary>
/// El almacén del render en caché: fragmentos <c>.mov</c> con su <c>.json</c> en una carpeta desechable
/// (<see cref="EnvironmentVariables.Render"/> o <c>%LOCALAPPDATA%\Rodaje\render</c>) y un índice en memoria huella →
/// cuadro. Escribe de forma atómica: un fragmento a medias nunca acierta.
/// </summary>
public interface IRenderCacheStore
{
    string Directory { get; }

    /// <summary>Cuántos cuadros distintos tiene el índice.</summary>
    int Count { get; }

    /// <summary>
    /// El cuadro de <paramref name="key"/>, si está. Con varios fragmentos para la misma huella se prefiere el que sigue a
    /// <paramref name="previous"/> (el cuadro anterior que se leyó), para no saltar de archivo en archivo.
    /// </summary>
    bool TryGet(RenderCacheKey key, RenderCacheHit? previous, out RenderCacheHit hit);

    /// <summary>Una ruta temporal donde escribir el <c>.mov</c> de un fragmento nuevo; no acierta hasta <see cref="Commit"/>.</summary>
    string CreateTemporaryPath();

    /// <summary>Publica el temporal con su <c>.json</c> (renombrando) y lo pone en el índice.</summary>
    RenderCacheFragment Commit(string temporaryMoviePath, RenderCacheFragmentMetadata metadata);

    /// <summary>Una lectura del fragmento falló (truncado o borrado por fuera): sale del índice.</summary>
    void Forget(RenderCacheFragment fragment);

    /// <summary>Bytes que ocupa el almacén en disco.</summary>
    long TotalBytes();

    RenderCacheCleanupReport Cleanup(RenderCacheCleanupOptions? options = null);
}
