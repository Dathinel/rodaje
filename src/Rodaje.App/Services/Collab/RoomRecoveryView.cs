using Dathinel.Sala;

namespace Rodaje.App.Services.Collab;

/// <summary>Decisiones y textos de las vistas de recuperación, sin depender de WinUI.</summary>
public static class RoomRecoveryView
{
    public static bool IsStandby(RoomMember member, string? standbyKey) =>
        !string.IsNullOrEmpty(standbyKey) && member.Key == standbyKey;

    public static bool CanHandOver(RoomState state, RoomMember member) =>
        state.IsHost && state.Phase == RoomPhase.Hosting && !member.IsMe && !member.IsHost
        && member.Role == Rol.Editar && member.Online;

    public static bool CanTakeHost(RoomState state, string? standbyKey) =>
        state.Phase == RoomPhase.Reconnecting && !state.IsHost && state.MyRole == Rol.Editar
        && !string.IsNullOrEmpty(standbyKey) && standbyKey == state.MyKey;

    public static string? HostNotice(RoomState state, string? standbyKey)
    {
        if (state.Phase != RoomPhase.Reconnecting || state.IsHost) return null;
        if (CanTakeHost(state, standbyKey)) return "El anfitrión se ha desconectado.";
        RoomMember? standby = state.Members.FirstOrDefault(m => IsStandby(m, standbyKey));
        return standby is null
            ? "El anfitrión se ha desconectado. Esperando a que vuelva la conexión; tus cambios se guardan aquí."
            : $"Esperando a que {standby.Alias} tome la sala; tus cambios se guardan aquí.";
    }

    public static string UnsettledCount(int count) => count switch
    {
        <= 0 => "",
        1 => "1 cambio no entró · Ver",
        _ => $"{count} cambios no entraron · Ver",
    };

    public static string HandOverConfirmation(string alias) => $"{alias} pasa a hospedar la sala. Tú sigues dentro como editor.";

    public const string KeepFailed = "No se pudo recuperar este cambio automáticamente. Sigue guardado aquí: puedes rehacerlo a mano o descartarlo. Si la sala terminó, vuelve a entrar para intentar conservarlo como nuevo.";

    public const string DiscardFailed = "No se pudo guardar el descarte. El cambio sigue en la lista. Comprueba que puedes guardar el proyecto y vuelve a intentarlo.";
}
