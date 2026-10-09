using Rodaje.Audio;
using Rodaje.Media.Gpu;

namespace Rodaje.Engine;

/// <summary>Opciones de <see cref="ClipPlayer"/>.</summary>
public sealed record ClipPlayerOptions
{
    /// <summary>Dispositivo del renderizador; los cuadros llegan como texturas suyas. Sin él se decodifica en la CPU.</summary>
    public GpuDevice? Device { get; init; }

    public bool AllowHardwareDecoding { get; init; } = true;

    /// <summary>Si es <c>false</c>, el reloj es el del sistema y no se abre ningún dispositivo de audio.</summary>
    public bool EnableAudio { get; init; } = true;

    /// <summary>Cuadros que se decodifican por delante del que toca mostrar.</summary>
    public int PrefetchFrames { get; init; } = 6;

    /// <summary>Cuadros que guarda la caché. Cada uno retiene una textura del pool de D3D11VA.</summary>
    /// <remarks>
    /// La caché es LRU y mostrar un cuadro lo renueva: un cuadro adelantado espera <c>PrefetchFrames - 1</c>
    /// refrescos mientras otros tantos se muestran y lo pasan. Con menos de <c>2 * PrefetchFrames</c> lugares la
    /// caché expulsa lo adelantado antes de verlo y cada cuadro termina en un seek.
    /// </remarks>
    public int CacheFrames { get; init; } = 16;

    /// <summary>Tope de memoria de la caché, para que un 4K de 10 bits no se coma una GPU de 4 GB.</summary>
    public long MaxCacheBytes { get; init; } = 384L * 1024 * 1024;

    /// <summary>Audio decodificado por delante de lo que suena.</summary>
    public TimeSpan AudioReadAhead { get; init; } = TimeSpan.FromMilliseconds(500);

    public AudioOutputOptions? AudioOutput { get; init; }

    /// <summary>Texturas de sobra del pool de D3D11VA: la caché, el cuadro en pantalla y los dos que retiene el decodificador.</summary>
    internal int ExtraHardwareFrames => CacheFrames + 4;

    /// <summary>
    /// <c>2 * prefetch</c> por la renovación LRU, más dos: el presupuesto se cuenta con el tamaño visible y las
    /// texturas reales traen relleno, así que caben uno o dos cuadros menos de los nominales.
    /// </summary>
    internal static int MinimumCacheFrames(int prefetch) => (2 * prefetch) + 2;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PrefetchFrames, nameof(PrefetchFrames));
        if (CacheFrames < MinimumCacheFrames(PrefetchFrames))
        {
            throw new ArgumentOutOfRangeException(nameof(CacheFrames),
                $"La caché ({CacheFrames} cuadros) debe guardar al menos {MinimumCacheFrames(PrefetchFrames)} con {PrefetchFrames} adelantados.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxCacheBytes, nameof(MaxCacheBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(AudioReadAhead, TimeSpan.FromMilliseconds(100), nameof(AudioReadAhead));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(AudioReadAhead, TimeSpan.FromSeconds(10), nameof(AudioReadAhead));
    }
}
