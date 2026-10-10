namespace Rodaje.App.Services.Media;

public sealed record ClipVisualsOptions
{
    /// <summary>Carpeta de la caché en disco (<c>miniaturas</c> y <c>ondas</c> dentro); nula para no usar disco.</summary>
    public string? CacheDirectory { get; init; }

    public long MemoryBudgetBytes { get; init; } = 128L << 20;

    public long ThumbnailDiskBudgetBytes { get; init; } = 512L << 20;

    public long WaveformDiskBudgetBytes { get; init; } = 128L << 20;

    /// <summary>Hilos que hacen miniaturas; cada uno con sus decodificadores.</summary>
    public int ThumbnailWorkers { get; init; } = 2;

    /// <summary>Decodificadores de miniaturas abiertos a la vez por hilo (uno por archivo).</summary>
    public int MaxOpenDecoders { get; init; } = 3;

    /// <summary>Formas de onda que se guardan en memoria.</summary>
    public int MaxWaveformsInMemory { get; init; } = 64;

    /// <summary>Un decodificador sin usar este tiempo se cierra.</summary>
    public TimeSpan DecoderIdleTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Carpeta de siempre: <c>%LOCALAPPDATA%\Rodaje\cache</c> (o la de <c>RODAJE_DATOS</c>).</summary>
    public static string DefaultCacheDirectory => Path.Combine(AppDataPaths.Root, "cache");
}
