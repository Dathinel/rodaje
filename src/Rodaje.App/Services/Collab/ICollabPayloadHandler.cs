using System.Text.Json;
using Dathinel.Sala;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// Lo que la sala le pide a Rodaje sobre las cargas (los parches de edición): validarlas en el anfitrión, aplicarlas en
/// todos en orden y retirar lo propio que no entró. Sala no mira dentro de la carga; la interfaz con los parches de Core
/// (B10) implementa esto.
/// </summary>
public interface ICollabPayloadHandler
{
    /// <summary>Solo en el anfitrión, de a una y en orden: comprueba la carga sobre una copia, sin aplicar nada.</summary>
    ValueTask<Veredicto> ValidateAsync(Miembro author, JsonElement payload, CancellationToken cancellationToken);

    /// <summary>En todos, por número de orden creciente: la carga entró. No debe bloquear ni lanzar.</summary>
    void Apply(Ordenado ordered);

    /// <summary>El anfitrión no aceptó una carga propia: hay que retirar la predicción local.</summary>
    void Rejected(Rechazo rejection);
}

/// <summary>El manejador de antes de que haya edición compartida: no acepta ninguna carga.</summary>
public sealed class NoEditingPayloadHandler : ICollabPayloadHandler
{
    public ValueTask<Veredicto> ValidateAsync(Miembro author, JsonElement payload, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Veredicto.No("sin-edicion", "Esta versión todavía no edita en grupo."));

    public void Apply(Ordenado ordered)
    {
    }

    public void Rejected(Rechazo rejection)
    {
    }
}
