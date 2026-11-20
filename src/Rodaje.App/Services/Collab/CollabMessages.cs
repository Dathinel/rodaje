using Dathinel.Sala;

namespace Rodaje.App.Services.Collab;

/// <summary>Un fallo de la colaboración con un texto que ya dice qué pasó y qué hacer; la interfaz solo lo enseña.</summary>
public sealed class CollabException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Los textos de la colaboración que salen de la lógica (errores y avisos). Sin códigos ni rutas del equipo.</summary>
public static class CollabMessages
{
    public static string ForInvite(InviteProblem problem) => problem switch
    {
        InviteProblem.Empty => "Pega aquí la invitación que te mandaron.",
        InviteProblem.NotAnInvite => "Eso no parece una invitación de Rodaje. Pídele a quien te invitó que te la mande otra vez y pégala tal cual.",
        InviteProblem.Damaged => "La invitación está incompleta o cambiada. Pídele a quien te invitó que te la mande otra vez.",
        InviteProblem.OtherApp => "Esa invitación es de otro programa, no de Rodaje.",
        InviteProblem.Expired => "La invitación ya caducó. Pídele a quien te invitó que cree una nueva.",
        _ => "",
    };

    /// <summary>El texto de un fallo de Sala. Los de versión y rechazo los redacta Sala o el anfitrión; aquí se completan.</summary>
    public static string For(SalaException ex) => ex.Codigo switch
    {
        "rechazado" => "Quien hospeda la sala no te dejó entrar. Si fue un error, pídele otra invitación.",
        "version" => string.IsNullOrWhiteSpace(ex.Message)
            ? "Tú y quien te invitó tenéis versiones distintas de Rodaje. Actualiza la que sea más vieja y vuelve a probar."
            : ex.Message,
        "billete-invalido" => "La invitación no vale: caducó, ya la usó otra persona o la anularon. Pídele a quien te invitó que cree una nueva.",
        "sin-conexion" => "No se pudo llegar al equipo de quien te invitó. Comprueba que Rodaje sigue abierto allí y que los dos tenéis internet o estáis en la misma red; luego vuelve a probar.",
        "llena" => "La sala está llena. Pídele a quien la hospeda que deje un hueco libre.",
        "ya-abierta" => "Ya hay una sala abierta en este Rodaje. Sal de ella antes de empezar otra.",
        "sin-red" => "Falta un componente de Rodaje para trabajar en grupo o no se pudo abrir. Reinstala Rodaje y vuelve a probar.",
        "ocupado" or "ocupado-red" => "Hay demasiado movimiento ahora mismo. Espera unos segundos y vuelve a probar.",
        "identidad" => "Rodaje no pudo leer la clave de este equipo para entrar en salas. Cierra y vuelve a abrir Rodaje; si sigue igual, avísanos.",
        "superada" => "La sala cambió de anfitrión mientras entrabas. Vuelve a pedir la invitación.",
        "cerrada" => "La sala se cerró.",
        _ => "No se pudo completar la conexión. Comprueba tu internet y vuelve a probar.",
    };

    /// <summary>Cualquier otra excepción que llegue desde Sala o desde el proceso de red.</summary>
    public static string ForUnexpected() =>
        "Algo falló al trabajar en grupo. Tu proyecto está a salvo. Cierra la sala y vuelve a empezar.";

    /// <summary>Por qué terminó la sala para este equipo (el motivo de <c>Sala.Cerrada</c>).</summary>
    public static string ForClosed(string reason) => reason switch
    {
        "expulsado" => "Te sacaron de la sala. Tu copia del proyecto sigue en este equipo.",
        "revocado" => "Tu acceso a la sala se anuló. Tu copia del proyecto sigue en este equipo.",
        "anfitrion-cerro" => "Quien hospeda la sala la cerró. Tu copia del proyecto sigue en este equipo.",
        "red" => "Se perdió la conexión con la sala y no se pudo recuperar. Tu copia del proyecto sigue en este equipo. Pide una invitación nueva para volver.",
        "desfasada" => "Estuviste desconectado demasiado tiempo y la sala siguió sin ti. Tu copia del proyecto sigue en este equipo. Pide una invitación nueva para volver a entrar.",
        "traspaso" => "La sala cambió de anfitrión y no se pudo seguir. Pide una invitación nueva para volver.",
        _ => "La sala terminó. Tu copia del proyecto sigue en este equipo.",
    };

    public static string Joined(string alias) => $"{alias} entró en la sala.";

    public static string Left(string alias, string reason) => reason switch
    {
        "expulsado" => $"{alias} fue expulsado de la sala.",
        "red" => $"{alias} se desconectó.",
        _ => $"{alias} salió de la sala.",
    };

    public static string RoleChanged(string alias, Rol role, bool me) =>
        me
            ? role == Rol.Mirar ? "Ahora solo puedes mirar en esta sala." : "Ahora puedes editar en esta sala."
            : role == Rol.Mirar ? $"{alias} ahora solo puede mirar." : $"{alias} ahora puede editar.";

    public static string Rejected(string reason, string? detail) => reason switch
    {
        "rol" => "No tienes permiso para editar en esta sala. Pídele a quien la hospeda que te deje editar.",
        "reserva" => "Otra persona tiene esa pista ahora. Espera a que termine.",
        "ritmo" => "Vas demasiado rápido. Espera un momento.",
        "tiempo" => "Quien hospeda la sala tardó demasiado en responder.",
        "ocupado" => "Hay demasiados cambios esperando. Espera unos segundos.",
        "conflicto" => "Otra persona cambió lo mismo un instante antes que tú.",
        _ => string.IsNullOrWhiteSpace(detail) ? "El cambio no es válido." : detail,
    };
}
