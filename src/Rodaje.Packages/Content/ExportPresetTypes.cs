namespace Rodaje.Packages.Content;

/// <summary>
/// <c>rodaje.exportpreset</c> v1: un preajuste de exportación tal como viene en un paquete. Es neutro (no conoce los
/// códecs del programa): los nombres de contenedor, códec y categoría son texto ya validado en forma, y cada programa los
/// traduce a sus ajustes y rechaza los que no tiene.
/// </summary>
/// <param name="Id">Id del archivo (el de la referencia es el del manifiesto).</param>
/// <param name="Name">Nombre para mostrar.</param>
/// <param name="Description">Una frase, o vacío.</param>
/// <param name="Category">Grupo de tarjetas: <c>platform</c>, <c>quality</c>, <c>master</c>, <c>web</c>, <c>audio</c>, <c>broadcast</c>, <c>cinema</c> o <c>archive</c>.</param>
/// <param name="Container">Contenedor en minúsculas: <c>mp4</c>, <c>mov</c>, <c>mkv</c>, <c>mxf</c>…</param>
public sealed record ExportPresetDefinition(string Id, string Name, string Description, string Category, string Container)
{
    public const string Format = "rodaje.exportpreset";
    public const int Version = 1;

    public ExportPresetVideo? Video { get; init; }

    public ExportPresetAudio? Audio { get; init; }

    /// <summary><c>moov</c> al principio en MP4/MOV.</summary>
    public bool FastStart { get; init; } = true;

    /// <summary>Capítulos desde los marcadores.</summary>
    public bool Chapters { get; init; }
}

/// <summary>El video de un <see cref="ExportPresetDefinition"/>.</summary>
/// <param name="Codec">En minúsculas: <c>h264</c>, <c>hevc</c>, <c>prores</c>, <c>dnxhr</c>, <c>ffv1</c>…</param>
public sealed record ExportPresetVideo(string Codec)
{
    public string? Profile { get; init; }

    public string? Level { get; init; }

    public string? SpeedPreset { get; init; }

    /// <summary>8, 10, 12 o 16.</summary>
    public int BitDepth { get; init; } = 8;

    /// <summary><c>420</c>, <c>422</c> o <c>444</c>.</summary>
    public string Chroma { get; init; } = "420";

    public bool Alpha { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>Tasa como fracción; 0/0 sigue la de la secuencia.</summary>
    public int FrameRateNumerator { get; init; }

    public int FrameRateDenominator { get; init; }

    /// <summary><c>progressive</c>, <c>tff</c> (campo superior primero) o <c>bff</c>.</summary>
    public string FieldOrder { get; init; } = "progressive";

    /// <summary><c>bars</c>, <c>crop</c> o <c>stretch</c>.</summary>
    public string Fit { get; init; } = "bars";

    /// <summary><c>crf</c>, <c>vbr</c>, <c>cbr</c>, <c>abr</c>, <c>lossless</c> o <c>profile</c>.</summary>
    public string RateMode { get; init; } = "crf";

    public double? Quality { get; init; }

    public int? BitrateKbps { get; init; }

    public int? MaxBitrateKbps { get; init; }

    public int? BufferKbps { get; init; }

    /// <summary>1 o 2.</summary>
    public int Passes { get; init; } = 1;

    public double? KeyframeSeconds { get; init; }

    public bool ClosedGop { get; init; } = true;

    public int? BFrames { get; init; }

    /// <summary><c>flat</c> o <c>equirectangular</c> (video 360 con metadatos esféricos).</summary>
    public string Projection { get; init; } = "flat";
}

/// <summary>El audio de un <see cref="ExportPresetDefinition"/>.</summary>
/// <param name="Codec">En minúsculas: <c>aac</c>, <c>opus</c>, <c>flac</c>, <c>pcm</c>…</param>
public sealed record ExportPresetAudio(string Codec)
{
    public int SampleRate { get; init; } = 48_000;

    public int Channels { get; init; } = 2;

    /// <summary><c>s16</c>, <c>s24</c>, <c>s32</c> o <c>float</c> (en PCM, FLAC y ALAC).</summary>
    public string SampleFormat { get; init; } = "s24";

    /// <summary><c>cbr</c>, <c>vbr</c>, <c>crf</c> (calidad) o <c>lossless</c>.</summary>
    public string RateMode { get; init; } = "cbr";

    public int? BitrateKbps { get; init; }

    public double? Quality { get; init; }

    /// <summary>Sonoridad integrada objetivo en LUFS, o <c>null</c> sin normalizar.</summary>
    public double? LoudnessLufs { get; init; }

    public double LoudnessTruePeak { get; init; } = -1;
}
