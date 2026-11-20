using Rodaje.Core.Model;

namespace Rodaje.Core.Collab;

/// <summary>Lo que cuenta de un elemento compartido: su padre y su contenido canónico.</summary>
public sealed record ElementState(string? Parent, string Content);

/// <summary>
/// Una fotografía de lo que se comparte de un proyecto: cada elemento con su contenido canónico (JSON sin rutas, con las
/// claves ordenadas) y el orden de las listas. No incluye el nombre del proyecto, las rutas de los medios ni lo que vive fuera
/// del <c>.rodaje</c>. Dos réplicas están iguales si y solo si su <see cref="Hash"/> coincide.
/// </summary>
public sealed class SharedState
{
    internal SharedState(string projectId, IReadOnlyDictionary<ElementKey, ElementState> elements, IReadOnlyDictionary<string, IReadOnlyList<string>> orders)
    {
        ProjectId = projectId;
        Elements = elements;
        Orders = orders;
    }

    public string ProjectId { get; }

    public IReadOnlyDictionary<ElementKey, ElementState> Elements { get; }

    /// <summary>Las listas ordenadas por su clave de contenedor (<see cref="PatchContainers"/>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Orders { get; }

    /// <summary>SHA-256 en hexadecimal del texto canónico de la fotografía.</summary>
    public string Hash => throw Pending.Exception();

    /// <summary>Saca la fotografía del proyecto tal como está ahora.</summary>
    public static SharedState Capture(Project project) => throw Pending.Exception();
}

internal static class Pending
{
    public static NotImplementedException Exception() =>
        new("Pendiente de la implementación de B10 (docs/propuestas/colaboracion-api-parches.md): este PR solo fija los tipos.");
}
