namespace Rodaje.App.Services.Collab;

/// <summary>Lo que se le dice a la persona al cerrar Rodaje con una sala abierta (la sala termina para quien hospeda).</summary>
public static class CollabClosing
{
    public const string Title = "Hay una sala abierta";

    /// <summary>El texto y el botón de la pregunta, o <c>null</c> si no hace falta preguntar (sin sala, o sola).</summary>
    public static (string Text, string Confirm)? Describe(RoomState? state)
    {
        if (state is not { InRoom: true })
        {
            return null;
        }

        int others = state.Members.Count(m => m.Online && !m.IsMe);
        if (state.IsHost)
        {
            return others == 0
                ? null
                : ($"Estás hospedando una sala con {others} {(others == 1 ? "persona" : "personas")}. Si cierras Rodaje, la sala termina para todos; cada una conserva su copia del proyecto.", "Cerrar Rodaje y la sala");
        }

        return ("Estás en una sala. Si cierras Rodaje, sales de ella; tu copia del proyecto se queda en este equipo.", "Cerrar Rodaje");
    }
}
