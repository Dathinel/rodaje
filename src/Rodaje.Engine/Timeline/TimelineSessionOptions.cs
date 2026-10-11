using Rodaje.Audio;
using Rodaje.Media.Proxies;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

/// <summary>Cuándo decodifica la vista previa el proxy de un medio en lugar del original.</summary>
public enum ProxyUsage
{
    /// <summary>Si hay un proxy listo y la política de proxies lo recomienda para ese medio (más de 1080p, por ejemplo).</summary>
    Auto,

    /// <summary>Siempre que haya un proxy listo.</summary>
    Always,

    /// <summary>Nunca: siempre el original.</summary>
    Never,
}

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

    /// <summary>Presupuesto de la caché de cuadros de todas las capas, dentro de <see cref="VramBudgetBytes"/>.</summary>
    public long MaxCacheBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Cuadros que puede retener un mismo decodificador; cada uno ocupa una textura de su pool.</summary>
    /// <remarks>Es el máximo: con la VRAM justa (por ejemplo, varias capas 4K) cada decodificador recibe menos.</remarks>
    public int MaxFramesPerSource { get; init; } = 24;

    /// <summary>
    /// VRAM que pueden ocupar entre todos los pools de D3D11VA (referencias, caché y cuadros en pantalla). Si es nula,
    /// 3/8 de la VRAM dedicada de la GPU (1,5 GB en una de 4 GB), entre 512 MB y 4 GB; sin dispositivo, 1,5 GB.
    /// </summary>
    public long? VramBudgetBytes { get; init; }

    /// <summary>Decodificadores de un mismo medio a la vez: varias pistas del mismo archivo en puntos distintos, y su pre-roll.</summary>
    public int MaxDecodersPerMedia { get; init; } = 4;

    /// <summary>Reserva para referencias lo que permite el nivel del flujo y no el peor caso (ver <c>VideoDecoderOptions</c>).</summary>
    public bool CompactReferencePool { get; init; } = true;

    /// <summary>Un decodificador que nadie necesita durante este tiempo se cierra; antes si hay muchos abiertos o falta VRAM.</summary>
    public TimeSpan DecoderIdleTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Cuándo usar los proxies de <see cref="ProxyResolver"/>; sin resolvedor, siempre los originales.</summary>
    public ProxyUsage UseProxies { get; init; } = ProxyUsage.Auto;

    /// <summary>
    /// Decide qué archivo decodifica la vista previa de cada medio. Cuando un proxy queda listo, la reproducción pasa a
    /// usarlo sola, sin cortes; la exportación nunca pasa por aquí y usa siempre los originales.
    /// </summary>
    public MediaSourceResolver? ProxyResolver { get; init; }

    /// <summary>LUT y transiciones de paquete; sin contenido, las capas salen sin LUT y las de paquete como fundido cruzado.</summary>
    public IEffectContent? Content { get; init; }

    /// <summary>
    /// Quien rasteriza los títulos en el dispositivo de la vista previa. El motor le pide los que se verán en los próximos
    /// <see cref="TitleLookahead"/> y, tras un seek, espera a los visibles como a cualquier capa. Sin él los títulos
    /// salen en la composición igual, sin precarga ni espera.
    /// </summary>
    public IGeneratedContentCache? Titles { get; init; }

    /// <summary>Con cuánta anticipación se piden los títulos que vienen.</summary>
    public TimeSpan TitleLookahead { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Tras un seek o al abrir, cuánto se sigue mostrando la última composición completa (o negro) mientras alguna capa
    /// no tiene su cuadro; pasado el tope se muestra lo que haya y se cuenta. Cero muestra siempre lo que haya.
    /// </summary>
    public TimeSpan PartialFrameTimeout { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Audio mezclado por delante de lo que suena.</summary>
    public TimeSpan AudioReadAhead { get; init; } = TimeSpan.FromMilliseconds(500);

    public AudioOutputOptions? AudioOutput { get; init; }

    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal const long DefaultVramBudget = 1536L * 1024 * 1024;

    /// <summary>El presupuesto de VRAM pedido o el que corresponde a una GPU con <paramref name="dedicatedVideoMemory"/> bytes propios.</summary>
    internal long ResolveVramBudget(long dedicatedVideoMemory) =>
        VramBudgetBytes ?? (dedicatedVideoMemory > 0
            ? Math.Clamp(dedicatedVideoMemory / 8 * 3, 512L * 1024 * 1024, 4096L * 1024 * 1024)
            : DefaultVramBudget);

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
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDecodersPerMedia, 1, nameof(MaxDecodersPerMedia));
        if (VramBudgetBytes is { } vram)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vram, nameof(VramBudgetBytes));
        }

        if (!Enum.IsDefined(UseProxies))
        {
            throw new ArgumentOutOfRangeException(nameof(UseProxies), UseProxies, "Uso de proxies desconocido.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(PartialFrameTimeout, TimeSpan.Zero, nameof(PartialFrameTimeout));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PartialFrameTimeout, TimeSpan.FromSeconds(5), nameof(PartialFrameTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThan(TitleLookahead, TimeSpan.Zero, nameof(TitleLookahead));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TitleLookahead, TimeSpan.FromSeconds(30), nameof(TitleLookahead));
        ArgumentOutOfRangeException.ThrowIfLessThan(AudioReadAhead, TimeSpan.FromMilliseconds(100), nameof(AudioReadAhead));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(AudioReadAhead, TimeSpan.FromSeconds(10), nameof(AudioReadAhead));
        ArgumentNullException.ThrowIfNull(TimeProvider);
    }
}
