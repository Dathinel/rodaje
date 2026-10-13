using System.Runtime.InteropServices;

namespace Rodaje.App.Services.Crashes;

/// <summary>
/// Cierra la sesión del registro de fallos (<see cref="CrashReporter.EndSession"/>) cuando Windows termina la sesión del
/// usuario (apagar, reiniciar, cerrar sesión: <c>WM_ENDSESSION</c>). Después de ese mensaje Windows puede terminar el
/// proceso sin volver de <c>Application.Start</c>, y la copia del registro quedaría como un falso «Rodaje se cerró por
/// un error». Subclase de la ventana con <c>SetWindowSubclass</c> y puntero a función (AOT).
/// </summary>
public static unsafe partial class SessionEndWatcher
{
    /// <summary><c>WM_ENDSESSION</c>.</summary>
    public const uint EndSessionMessage = 0x0016;

    private const nuint SubclassId = 0x524F44; // «ROD»

    private static Action? s_onEnd;

    /// <summary>Engancha la ventana <paramref name="hwnd"/> (una vez por proceso; fuera de Windows no hace nada).</summary>
    public static bool Install(nint hwnd, Action onSessionEnd)
    {
        ArgumentNullException.ThrowIfNull(onSessionEnd);
        if (!OperatingSystem.IsWindows() || hwnd == 0 || s_onEnd is not null)
        {
            return false;
        }

        s_onEnd = onSessionEnd;
        return SetWindowSubclass(hwnd, &OnMessage, SubclassId, 0);
    }

    /// <summary>
    /// Atiende un mensaje de la ventana: con <c>WM_ENDSESSION</c> y <paramref name="wParam"/> distinto de cero (la sesión
    /// termina de verdad) llama a <paramref name="onSessionEnd"/>. Devuelve si lo llamó. Nunca lanza.
    /// </summary>
    public static bool Handle(uint message, nuint wParam, Action? onSessionEnd)
    {
        if (message != EndSessionMessage || wParam == 0 || onSessionEnd is null)
        {
            return false;
        }

        try
        {
            onSessionEnd();
        }
#pragma warning disable CA1031 // Un procedimiento de ventana no deja escapar excepciones a Windows.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return true;
    }

    [UnmanagedCallersOnly]
    private static nint OnMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        _ = Handle(message, wParam, s_onEnd);
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [LibraryImport("comctl32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nuint, nint, nuint, nuint, nint> proc, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
