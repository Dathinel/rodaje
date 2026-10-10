using Rodaje.Audio;

namespace Rodaje.Engine.Timeline;

/// <summary>Opciones de <see cref="TimelineSession"/>.</summary>
public sealed record TimelineSessionOptions
{
    /// <summary>Carpeta contra la que se resuelven las rutas relativas de los medios; si es nula, la del proceso.</summary>
    public string? ProjectDirectory { get; init; }

    public bool AllowHardwareDecoding { get; init; } = true;

    /// <summary>Si es <c>false</c>, no se mezcla audio y el reloj es el del sistema.</summary>
    public bool EnableAudio { get; init; } = true;

    /// <summary>Cuadros que cada capa decodifica por delante, contando el que toca mostrar.</summary>
    public int PrefetchFrames { get; init; } = 8;

    /// <summary>Con cuánta anticipación se empieza a decodificar el clip que sigue en cada pista.</summary>
    public TimeSpan PreRoll { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Cuadros del inicio del clip siguiente que se tienen listos antes del corte.</summary>
    public int PreRollFrames { get; init; } = 6;

    /// <summary>Presupuesto de la caché de cuadros de todas las capas: la mayor parte de la VRAM libre de una GPU de 4 GB.</summary>
    public long MaxCacheBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Cuadros que puede retener un mismo origen; cada uno ocupa una textura del pool de su decodificador.</summary>
    public int MaxFramesPerSource { get; init; } = 24;

    /// <summary>Un decodificador que nadie necesita durante este tiempo se cierra.</summary>
    public TimeSpan DecoderIdleTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Audio mezclado por delante de lo que suena.</summary>
    public TimeSpan AudioReadAhead { get; init; } = TimeSpan.FromMilliseconds(500);

    public AudioOutputOptions? AudioOutput { get; init; }

    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Texturas de sobra del pool de cada decodificador: lo que retiene la caché, el cuadro en pantalla de cada capa y
    /// la composición que todavía se dibuja, más los dos que retiene el propio decodificador.
    /// </summary>
    internal int ExtraHardwareFrames => MaxFramesPerSource + 8;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PrefetchFrames, nameof(PrefetchFrames));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PreRollFrames, nameof(PreRollFrames));
        ArgumentOutOfRangeException.ThrowIfLessThan(PreRoll, TimeSpan.Zero, nameof(PreRoll));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PreRoll, TimeSpan.FromSeconds(10), nameof(PreRoll));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxCacheBytes, nameof(MaxCacheBytes));
        if (MaxFramesPerSource < PrefetchFrames + PreRollFrames + 2)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFramesPerSource),
                $"Cada origen debe poder guardar al menos {PrefetchFrames + PreRollFrames + 2} cuadros (adelanto, pre-roll y dos de margen); se pidieron {MaxFramesPerSource}.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(DecoderIdleTimeout, TimeSpan.Zero, nameof(DecoderIdleTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThan(AudioReadAhead, TimeSpan.FromMilliseconds(100), nameof(AudioReadAhead));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(AudioReadAhead, TimeSpan.FromSeconds(10), nameof(AudioReadAhead));
        ArgumentNullException.ThrowIfNull(TimeProvider);
    }
}
