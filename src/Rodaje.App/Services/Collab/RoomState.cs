using Dathinel.Sala;

namespace Rodaje.App.Services.Collab;

public enum RoomPhase
{
    /// <summary>Sin sala.</summary>
    Closed,

    /// <summary>Abriendo la sala (anfitrión) o conectando y esperando que te dejen entrar (invitado).</summary>
    Starting,

    /// <summary>Anfitrión con la sala abierta.</summary>
    Hosting,

    /// <summary>Invitado dentro.</summary>
    Connected,

    /// <summary>Invitado sin conexión con el anfitrión: reintenta solo.</summary>
    Reconnecting,

    /// <summary>La sala terminó para este equipo; <see cref="RoomState.Problem"/> dice por qué.</summary>
    Ended,
}

/// <summary>Una persona de la sala.</summary>
/// <param name="ColorIndex">0 a 11, siempre el mismo para la misma clave en todos los equipos.</param>
/// <param name="Phrase">Las cuatro palabras de la clave, para comprobarla de viva voz.</param>
/// <param name="Online">Está conectado ahora. Sala guarda a los que ya dejó entrar aunque se vayan; el anfitrión sabe quién se fue, el invitado de momento no.</param>
public sealed record RoomMember(string Key, string Alias, Rol Role, bool IsHost, bool IsMe, string Phrase, int ColorIndex, bool Online = true);

/// <summary>Alguien que pide entrar y espera la decisión del anfitrión.</summary>
public sealed record RoomRequest(string Key, string Alias, string Phrase);

/// <summary>Un aviso para la persona: alguien entró, salió, un cambio no entró…</summary>
public sealed record RoomNotice(DateTimeOffset At, string Text, bool IsProblem);

/// <summary>
/// La sala vista desde la interfaz. Es inmutable: cada cambio es un objeto nuevo, así se puede leer desde cualquier hilo
/// y la vista solo repinta cuando llega <see cref="RoomService.StateChanged"/>.
/// </summary>
public sealed record RoomState(
    RoomPhase Phase,
    bool IsHost,
    string MyKey,
    string MyAlias,
    Rol MyRole,
    IReadOnlyList<RoomMember> Members,
    IReadOnlyList<RoomRequest> Requests,
    string? Problem)
{
    public static readonly RoomState Closed = new(RoomPhase.Closed, false, "", "", Rol.Editar, [], [], null);

    /// <summary>Cuántos miembros están conectados ahora.</summary>
    public int OnlineCount => Members.Count(m => m.Online);

    public bool InRoom => Phase is RoomPhase.Hosting or RoomPhase.Connected or RoomPhase.Reconnecting;

    /// <summary>Puede mandar cambios: está dentro, con rol de editar y con conexión.</summary>
    public bool CanEdit => MyRole == Rol.Editar && Phase is RoomPhase.Hosting or RoomPhase.Connected;
}

/// <summary>Color estable de cada miembro: sale de la clave, no del orden de llegada.</summary>
public static class MemberColors
{
    public const int Count = 12;

    public static int IndexFor(string key)
    {
        // Los 8 primeros hexadecimales de la clave (32 bits) repartidos entre los colores.
        uint v = 0;
        foreach (char c in key.AsSpan(0, Math.Min(8, key.Length)))
        {
            v = (v << 4) | (uint)(c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10);
        }

        return (int)(v % Count);
    }
}
