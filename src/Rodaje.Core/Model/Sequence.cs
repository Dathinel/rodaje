using System.Text.Json.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>Espacio de color en que se trabaja, se ve y se exporta una secuencia (formato 15).</summary>
public enum SequenceColorSpace
{
    /// <summary>SDR BT.709: lo de siempre. Los medios HDR se convierten al entrar (D-052).</summary>
    [JsonStringEnumMemberName("sdr-709")]
    Sdr709,

    /// <summary>HDR10: BT.2020 con curva PQ (ST 2084).</summary>
    [JsonStringEnumMemberName("hdr-pq")]
    HdrPq,

    /// <summary>BT.2020 con curva HLG (ARIB STD-B67).</summary>
    [JsonStringEnumMemberName("hdr-hlg")]
    HdrHlg,
}

/// <summary>Línea de tiempo; el índice 0 de <see cref="VideoTracks"/> es la capa de más abajo.</summary>
public sealed class Sequence
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public Rational FrameRate { get; set; } = Rational.Fps30;

    /// <summary>
    /// Último instante de la línea de tiempo: ningún clip termina después (2^62 ticks, unos 207 años). Deja la mitad del
    /// rango de un <c>long</c> de margen, así sumar dos instantes válidos o llevar el cursor al cuadro siguiente nunca
    /// desborda. Los marcadores y las regiones no cuentan para el largo de la secuencia ni para el cursor, así que pueden
    /// llegar a <see cref="long.MaxValue"/>; una edición que los correría fuera del rango se rechaza (EditException).
    /// </summary>
    public static Time MaxTime { get; } = Time.FromTicks(1L << 62);

    /// <summary>Tasas de cuadros que admite una secuencia: de 1 a 1000 fps, las mismas que acepta la sonda de medios.</summary>
    public static bool IsValidFrameRate(Rational rate) =>
        rate.Num > 0 && rate.Den > 0 && rate.Num >= rate.Den && rate.Num <= (Int128)MaxFrameRate * rate.Den;

    /// <summary>Cuadros por segundo más altos de una secuencia (<see cref="IsValidFrameRate"/>).</summary>
    public const int MaxFrameRate = 1000;

    /// <summary>
    /// Cuadros por segundo más bajos que admite el modelo: uno cada 5 segundos. La v1.0.0 guardaba tasas por debajo de 1
    /// (fotos fijas); con un cuadro de hasta 5 s, cualquier número de cuadro de 32 bits cabe en un <see cref="Time"/>.
    /// </summary>
    public static Rational MinFrameRate { get; } = new(1, 5);

    /// <summary>
    /// Tasas que acepta el modelo al abrir y al editar: de <see cref="MinFrameRate"/> a <see cref="MaxFrameRate"/>. El
    /// selector de formato y la exportación siguen pidiendo <see cref="IsValidFrameRate"/>.
    /// </summary>
    public static bool IsSupportedFrameRate(Rational rate) =>
        rate.Num > 0 && rate.Den > 0 && (Int128)rate.Num * MinFrameRate.Den >= rate.Den && rate.Num <= (Int128)MaxFrameRate * rate.Den;

    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    public int SampleRate { get; set; } = 48000;

    /// <summary>Por defecto SDR; no se escribe en el archivo (y el formato se queda en 13 o 14) si todas las secuencias son SDR.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SequenceColorSpace ColorSpace { get; set; }

    public const double MinMasterGainDb = -60;

    public const double MaxMasterGainDb = 24;

    /// <summary>Ganancia de toda la mezcla en dB, antes del limitador (la pone «Normalizar la mezcla»). Por defecto 0.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double MasterGainDb { get; set; }

    /// <summary>Efectos de audio de toda la mezcla (ecualizador y compresor), después de la ganancia maestra; keyframes en tiempo de la secuencia.</summary>
    [JsonIgnore]
    public List<EffectInstance> MasterEffects { get; set; } = [];

    /// <summary><see cref="MasterEffects"/> en el archivo: no se escribe si está vacía.</summary>
    [JsonPropertyName("masterEffects")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public List<EffectInstance>? MasterEffectsInFile
    {
        get => MasterEffects.Count > 0 ? MasterEffects : null;
        set => MasterEffects = value ?? [];
    }

    /// <summary>Buses de audio (submezclas), en el orden del mezclador (formato 7).</summary>
    [JsonIgnore]
    public List<AudioBus> AudioBuses { get; set; } = [];

    /// <summary><see cref="AudioBuses"/> en el archivo: no se escribe si está vacía.</summary>
    [JsonPropertyName("audioBuses")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public List<AudioBus>? AudioBusesInFile
    {
        get => AudioBuses.Count > 0 ? AudioBuses : null;
        set => AudioBuses = value ?? [];
    }

    public List<Track> VideoTracks { get; set; } = [];

    public List<Track> AudioTracks { get; set; } = [];

    /// <summary>Marcadores ordenados por tiempo.</summary>
    public List<Marker> Markers { get; set; } = [];

    /// <summary>Regiones ordenadas por inicio; pueden solaparse.</summary>
    public List<Region> Regions { get; set; } = [];

    [JsonIgnore]
    public IEnumerable<Track> AllTracks => VideoTracks.Concat(AudioTracks);

    /// <summary>Final del último clip de cualquier pista.</summary>
    [JsonIgnore]
    public Time Duration => AllTracks.SelectMany(t => t.Clips).Select(c => c.End).DefaultIfEmpty(Time.Zero).Max();

    public Sequence Clone()
    {
        Sequence copy = (Sequence)MemberwiseClone();
        copy.VideoTracks = VideoTracks.ConvertAll(t => t.Clone());
        copy.AudioTracks = AudioTracks.ConvertAll(t => t.Clone());
        copy.MasterEffects = MasterEffects.ConvertAll(e => e.Clone());
        copy.AudioBuses = AudioBuses.ConvertAll(b => b.Clone());
        copy.Markers = Markers.ConvertAll(m => m.Clone());
        copy.Regions = Regions.ConvertAll(r => r.Clone());
        return copy;
    }

    public Track? FindTrack(string trackId) => AllTracks.FirstOrDefault(t => t.Id == trackId);

    public AudioBus? FindBus(string busId) => AudioBuses.Find(b => b.Id == busId);

    public Track GetTrack(string trackId) =>
        FindTrack(trackId) ?? throw new KeyNotFoundException($"No existe la pista «{trackId}» en la secuencia.");

    /// <summary>Busca un clip y la pista que lo contiene.</summary>
    public (Track Track, Clip Clip)? FindClip(string clipId)
    {
        foreach (Track track in AllTracks)
        {
            Clip? clip = track.Clips.Find(c => c.Id == clipId);
            if (clip is not null)
            {
                return (track, clip);
            }
        }

        return null;
    }

    public (Track Track, Clip Clip) GetClip(string clipId) =>
        FindClip(clipId) ?? throw new KeyNotFoundException($"No existe el clip «{clipId}» en la secuencia.");

    /// <summary>El clip y los enlazados con él; el clip pedido va primero.</summary>
    public List<(Track Track, Clip Clip)> GetLinkGroup(string clipId)
    {
        (Track track, Clip clip) = GetClip(clipId);
        var group = new List<(Track, Clip)> { (track, clip) };
        if (clip.LinkId is null)
        {
            return group;
        }

        foreach (Track t in AllTracks)
        {
            foreach (Clip c in t.Clips)
            {
                if (c.LinkId == clip.LinkId && c.Id != clip.Id)
                {
                    group.Add((t, c));
                }
            }
        }

        return group;
    }
}
