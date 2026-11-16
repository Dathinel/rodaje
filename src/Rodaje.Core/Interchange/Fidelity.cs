using Rodaje.Core.Model;

namespace Rodaje.Core.Interchange;

/// <summary>
/// Qué sobrevive de una secuencia en cada formato (tabla de <c>docs/intercambio.md</c>). De esta misma regla salen el
/// informe de pérdidas y la prueba de ida y vuelta: <c>Read(Write(s))</c> debe ser equivalente a <see cref="Project"/>.
/// </summary>
public static class Fidelity
{
    /// <summary>
    /// La secuencia como debería volver después de escribirla en <paramref name="format"/> y leerla, con sus medios y sus
    /// compuestos; cada cosa que quita o aproxima la anota en <paramref name="report"/>.
    /// </summary>
    public static TimelineDraft Project(Model.Project project, Sequence sequence, InterchangeFormat format, InterchangeOptions options, InterchangeReport report) =>
        throw new NotImplementedException("Fidelity: pendiente (fase F3 de D-062).");

    /// <summary>
    /// Nulo si las dos son iguales sin mirar los identificadores (se emparejan por posición); si no, la primera diferencia
    /// en una frase.
    /// </summary>
    public static string? Difference(TimelineDraft expected, TimelineDraft actual) =>
        throw new NotImplementedException("Fidelity: pendiente (fase F3 de D-062).");
}
