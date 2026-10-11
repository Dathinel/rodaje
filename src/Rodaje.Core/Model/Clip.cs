using System.Text.Json.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>
/// Fragmento de un medio, <c>[SourceIn, SourceOut)</c>, colocado en <see cref="Start"/> de su pista. Con
/// <see cref="Speed"/>, la relación entre los dos tiempos es <see cref="TimeMap"/>. Un clip con
/// <see cref="Generator"/> (formato 5) no tiene medio: su origen es virtual y <see cref="SourceIn"/> puede ser negativo.
/// </summary>
public sealed class Clip
{
    public string Id { get; set; } = "";

    /// <summary>Medio del clip; vacío en un clip con <see cref="Generator"/>.</summary>
    [JsonIgnore]
    public string MediaId { get; set; } = "";

    // Solo en el JSON: un generador no escribe «mediaId».
    [JsonInclude]
    [JsonPropertyName("mediaId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal string? StoredMediaId
    {
        get => Generator is not null && MediaId.Length == 0 ? null : MediaId;
        set => MediaId = value ?? "";
    }

    /// <summary>Punto de entrada dentro del archivo de origen (incluido).</summary>
    public Time SourceIn { get; set; }

    /// <summary>Punto de salida dentro del archivo de origen (excluido).</summary>
    public Time SourceOut { get; set; }

    /// <summary>Posición del clip en la pista.</summary>
    public Time Start { get; set; }

    // Solo en el JSON: «duration» se escribe cuando hay velocidad.
    [JsonInclude]
    [JsonPropertyName("duration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal Time? StoredDuration
    {
        get => Speed is null ? null : _duration;
        set => _duration = value ?? default;
    }

    /// <summary>Los clips con el mismo enlace se editan juntos; <c>null</c> si el clip va suelto.</summary>
    public string? LinkId { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Velocidad, reversa o congelado; nula a 1×, como en el formato 3.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClipSpeed? Speed { get; set; }

    /// <summary>Contenido generado (un título) en lugar de un medio; nulo en un clip de medio.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClipGenerator? Generator { get; set; }

    /// <summary>Efectos aplicados en orden. Sus keyframes están en tiempo del origen.</summary>
    public List<EffectInstance> Effects { get; set; } = [];

    private Time _duration;

    /// <summary>Duración en la pista. Sin <see cref="Speed"/> es <see cref="SourceDuration"/>; con ella se guarda.</summary>
    [JsonIgnore]
    public Time Duration
    {
        get => Speed is null ? SourceOut - SourceIn : _duration;
        set => _duration = value;
    }

    /// <summary>Largo del tramo del origen, <c>SourceOut − SourceIn</c>.</summary>
    [JsonIgnore]
    public Time SourceDuration => SourceOut - SourceIn;

    [JsonIgnore]
    public Time End => Start + Duration;

    /// <summary>Mapeo entre la línea de tiempo y el origen; se calcula en cada llamada.</summary>
    [JsonIgnore]
    public ClipTimeMap TimeMap => ClipTimeMap.Of(this);

    /// <summary>
    /// Instante del origen para un instante de la línea de tiempo. Fuera del clip sigue a la velocidad del borde
    /// (<see cref="ClipTimeMap.ExtendedSource"/>); sin velocidad es <c>SourceIn + (t − Start)</c>.
    /// </summary>
    public Time ToSourceTime(Time timelineTime) =>
        Speed is null ? SourceIn + (timelineTime - Start) : TimeMap.ExtendedSource(timelineTime);

    /// <summary>Instante en que se evalúan los keyframes del clip en <paramref name="timelineTime"/> (acotado al clip).</summary>
    public Time ToEffectTime(Time timelineTime) => TimeMap.EffectTime(timelineTime);

    public Clip Clone()
    {
        Clip copy = (Clip)MemberwiseClone();
        copy.Effects = Effects.ConvertAll(e => e.Clone());
        copy.Speed = Speed?.Clone();
        copy.Generator = Generator?.Clone();
        return copy;
    }
}
