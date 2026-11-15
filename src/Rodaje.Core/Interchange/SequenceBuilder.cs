using Rodaje.Core.Model;

namespace Rodaje.Core.Interchange;

/// <summary>Lo común al leer cualquier formato: fusión con el proyecto abierto, identificadores nuevos y validación.</summary>
public static class SequenceBuilder
{
    /// <summary>
    /// Prepara <paramref name="draft"/> para añadirlo a <paramref name="target"/> sin tocarlo: identificadores nuevos para
    /// todo (medios, secuencias, pistas, clips, enlaces, grupos, transiciones y marcadores), medios deduplicados por ruta
    /// normalizada y reutilizados si el proyecto ya los tiene, solapes sin transición resueltos con aviso, y el conjunto
    /// validado con <see cref="ModelValidator"/>. Lanza <see cref="InterchangeException"/> si no es válido.
    /// </summary>
    public static InterchangeResult Build(TimelineDraft draft, Project target, IIdGenerator ids, InterchangeReport report) =>
        throw new NotImplementedException("SequenceBuilder: pendiente (fase F3 de D-062).");

    /// <summary>
    /// Ruta local de un medio importado: <c>file://</c> con <c>%20</c> y tildes, relativa al archivo importado o absoluta.
    /// Nula si es de red (UNC o URL que no es <c>file</c>): no se sondea sola.
    /// </summary>
    public static string? NormalizePath(string raw, string? baseDirectory) =>
        throw new NotImplementedException("SequenceBuilder: pendiente (fase F3 de D-062).");
}
