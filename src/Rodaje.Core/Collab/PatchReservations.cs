using Rodaje.Core.Model;

namespace Rodaje.Core.Collab;

/// <summary>Qué reservas de la sala necesita un parche (reservas por pista: <c>colaboracion.md</c> §3.2).</summary>
public static class PatchReservations
{
    /// <summary>Reserva de lo global: cabecera de secuencia, buses, mezcla maestra, marcadores, regiones, listas y medios.</summary>
    public const string Project = "proyecto";

    public static string Track(string trackId) => "pista:" + trackId;

    /// <summary>
    /// Las reservas que exige el parche sobre el proyecto <paramref name="current"/> (el del anfitrión o el del invitado antes de
    /// aplicarlo): <c>pista:&lt;id&gt;</c> por cada pista que toca (la de un clip nuevo, la de donde sale y la de donde llega) y
    /// <c>proyecto</c> por todo lo demás. Añadir un medio nuevo no pide ninguna.
    /// </summary>
    public static IReadOnlySet<string> Required(Patch patch, Project current) => throw Pending.Exception();
}
