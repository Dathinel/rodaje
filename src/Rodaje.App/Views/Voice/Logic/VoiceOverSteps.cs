using Rodaje.Engine.Recording;

namespace Rodaje.App.Views.Voice;

/// <summary>Qué hace el diálogo de la voz en off en un tick del temporizador (cada 100 ms).</summary>
public enum VoiceOverTickAction
{
    /// <summary>Cuenta atrás entre dos segundos: no cambia nada.</summary>
    Wait,

    /// <summary>Cuenta atrás: muestra los segundos que quedan.</summary>
    Countdown,

    /// <summary>La cuenta llegó a cero: silencia la pista, empieza a grabar y a sonar, y muestra lo grabado.</summary>
    StartRecording,

    /// <summary>Grabando: muestra lo grabado y el nivel.</summary>
    Update,

    /// <summary>Falló la escritura a mitad de la toma (por ejemplo, el disco se llenó): para el temporizador y cierra.</summary>
    Stop,
}

/// <summary>El estado del temporizador del diálogo: ticks contados, segundos que quedan de cuenta atrás y si ya graba.</summary>
public readonly record struct VoiceOverTickState(long Ticks, int Remaining, bool Recording)
{
    public const int TicksPerSecond = 10;

    public static VoiceOverTickState Countdown(int seconds) => new(0, seconds, false);
}

/// <summary>Cómo quedó la toma al cerrar el diálogo.</summary>
/// <param name="Take">La toma, o nula si <c>Finish</c> o al soltar el grabador falló.</param>
/// <param name="Failure">El primer error al cerrar el WAV o al soltar el grabador, o nulo.</param>
public sealed record VoiceOverClosed(VoiceOverOutcome Outcome, VoiceOverTake? Take, IOException? Failure);

/// <summary>
/// La lógica del diálogo «Grabar voz en off» que no depende de WinUI: qué hacer en cada tick del temporizador y qué
/// archivo queda por borrar al cerrar.
/// </summary>
public static class VoiceOverSteps
{
    /// <summary>
    /// El tick siguiente: en la cuenta atrás, un segundo cada <see cref="VoiceOverTickState.TicksPerSecond"/> ticks y, al
    /// llegar a cero, empezar a grabar; grabando, parar en cuanto el grabador tenga un error de escritura
    /// (<paramref name="failed"/>) en vez de seguir contando hasta que se pulse «Detener».
    /// </summary>
    public static (VoiceOverTickState State, VoiceOverTickAction Action) Tick(VoiceOverTickState state, bool failed)
    {
        state = state with { Ticks = state.Ticks + 1 };
        if (!state.Recording)
        {
            if (state.Ticks % VoiceOverTickState.TicksPerSecond != 0)
            {
                return (state, VoiceOverTickAction.Wait);
            }

            state = state with { Remaining = state.Remaining - 1 };
            if (state.Remaining > 0)
            {
                return (state, VoiceOverTickAction.Countdown);
            }

            // El grabador acaba de empezar: no puede tener todavía un error de escritura.
            return (state with { Recording = true }, VoiceOverTickAction.StartRecording);
        }

        return (state, failed ? VoiceOverTickAction.Stop : VoiceOverTickAction.Update);
    }

    /// <summary>
    /// El archivo que hay que borrar al cerrar: el WAV del grabador (<paramref name="recorderPath"/>) salvo que la toma entre
    /// en la línea de tiempo. También cuando <c>Finish</c> falló y no hay toma: el grabador ya había creado el WAV, y con
    /// el disco lleno no se puede dejar ahí un archivo a medias.
    /// </summary>
    public static string? FileToDelete(VoiceOverOutcome outcome, string recorderPath) =>
        outcome == VoiceOverOutcome.Insert ? null : recorderPath;

    /// <summary>
    /// Cierra la toma: <paramref name="finish"/> (que puede lanzar <see cref="IOException"/>), <paramref name="dispose"/>
    /// y, ya soltado el archivo, <paramref name="delete"/> del WAV si no va a la línea de tiempo. Si
    /// <paramref name="dispose"/> lanza (<see cref="IOException"/> o <see cref="UnauthorizedAccessException"/>), la
    /// toma no entra, se avisa de ese error si no había otro y el WAV se borra igual.
    /// </summary>
    public static VoiceOverClosed Close(bool cancelled, string recorderPath, Func<VoiceOverTake> finish, Action dispose, Action<string> delete)
    {
        ArgumentNullException.ThrowIfNull(finish);
        ArgumentNullException.ThrowIfNull(dispose);
        ArgumentNullException.ThrowIfNull(delete);
        VoiceOverTake? take = null;
        IOException? failure = null;
        try
        {
            take = finish();
        }
        catch (IOException ex)
        {
            failure = ex;
        }

        try
        {
            dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Con el disco lleno, soltar el grabador vuelve a fallar al volcar el búfer. No escapa a la interfaz: se
            // avisa (si Finish no había fallado ya) y el WAV se borra igual, porque ya no se puede fiar de él.
            failure ??= ex as IOException ?? new IOException("No se pudo cerrar la voz en off: " + ex.Message, ex);
            take = null;
        }

        VoiceOverOutcome outcome = VoiceOverRecorder.Decide(cancelled, failure is not null, take?.Frames ?? 0);
        if (FileToDelete(outcome, recorderPath) is { } path)
        {
            delete(path);
        }

        return new VoiceOverClosed(outcome, take, failure);
    }
}
