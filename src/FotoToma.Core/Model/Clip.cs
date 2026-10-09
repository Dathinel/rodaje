using FotoToma.Core.Timing;

namespace FotoToma.Core.Model;

/// <summary>
/// Un fragmento de un medio colocado en la línea de tiempo.
/// </summary>
/// <remarks>
/// Guarda tres instantes: dónde empieza y termina el fragmento dentro del archivo de origen
/// (<see cref="SourceIn"/>, <see cref="SourceOut"/>) y dónde se coloca en la pista
/// (<see cref="Start"/>). Todas las operaciones de edición son cambios de estos tres números, por eso
/// la edición es no destructiva.
/// </remarks>
public sealed class Clip
{
    public string Id { get; set; } = "";

    public string MediaId { get; set; } = "";

    /// <summary>Punto de entrada dentro del archivo de origen (incluido).</summary>
    public Time SourceIn { get; set; }

    /// <summary>Punto de salida dentro del archivo de origen (excluido).</summary>
    public Time SourceOut { get; set; }

    /// <summary>Posición del clip en la pista.</summary>
    public Time Start { get; set; }

    /// <summary>
    /// Los clips con el mismo identificador de enlace se mueven y recortan juntos, como el video y el
    /// audio de una misma toma. <c>null</c> si el clip va suelto.
    /// </summary>
    public string? LinkId { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Efectos aplicados en orden. Sus keyframes están en tiempo del origen.</summary>
    public List<EffectInstance> Effects { get; set; } = [];

    public Time Duration => SourceOut - SourceIn;

    public Time End => Start + Duration;

    /// <summary>Convierte un instante de la línea de tiempo al instante correspondiente del origen.</summary>
    public Time ToSourceTime(Time timelineTime) => SourceIn + (timelineTime - Start);

    public Clip Clone()
    {
        Clip copy = (Clip)MemberwiseClone();
        copy.Effects = Effects.ConvertAll(e => e.Clone());
        return copy;
    }
}
