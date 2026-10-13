using System.Diagnostics;

namespace Rodaje.App.Services.Crashes;

/// <summary>Informa una excepción (<c>Program.ReportException</c>); <paramref name="repeated"/> dice que era una repetición sin informe propio.</summary>
public delegate string? ExceptionReport(CrashKind kind, Exception exception, bool fatal, out bool repeated);

/// <summary>Informa un error del que solo hay texto (<see cref="CrashReporter.Write(CrashKind, string, bool, out bool)"/>).</summary>
public delegate string? TextReport(CrashKind kind, string? details, bool fatal, out bool repeated);

/// <summary>
/// El cableado de las vías de excepción, sin WinUI para poder probarlo: en qué orden se enganchan
/// (<see cref="Install"/>, desde <c>Program</c>) y qué se hace con un error del hilo de la interfaz o de XAML, con
/// ventana o sin ella (<c>App</c>).
/// </summary>
public static class CrashRouting
{
    /// <summary>Cuánto espera la lectura de la GPU para el informe, fuera del arranque.</summary>
    public static readonly TimeSpan GpuWarmUpDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Engancha las vías de excepción y el filtro nativo antes que nada (un fallo al leer los ajustes también deja rastro),
    /// después el registro de <see cref="Trace"/> y el cierre del proceso. No abre la copia del registro ni recupera
    /// sesiones: eso es <see cref="CrashReporter.BeginSession"/>, que espera a las opciones (<c>AppOptions.Parse</c>).
    /// </summary>
    public static void Install(CrashReporter crashes, CrashHooks hooks, Action<string?> appendErrorFile, string commandLine)
    {
        ArgumentNullException.ThrowIfNull(crashes);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(appendErrorFile);
        hooks.OnUnhandled((sender, e) =>
        {
            appendErrorFile(e.ExceptionObject?.ToString());
            crashes.OnAppDomainUnhandled(sender, e);
        });
        hooks.OnUnobservedTask(crashes.OnUnobservedTask);
        hooks.InstallNative(crashes);
        hooks.AddTraceListener(new SessionLogTraceListener(crashes.Log));
        crashes.Log.Append($"Rodaje {crashes.Version} arranca: {commandLine}");
        // Un proceso que sale normalmente (Environment.Exit incluido) también cierra la sesión.
        hooks.OnProcessExit((_, _) => crashes.EndSession());
        // La GPU se lee ahora, fuera de cualquier fallo: un fallo nativo no debe tocar DXGI.
        hooks.RunLater(GpuWarmUpDelay, crashes.WarmUp);
    }

    /// <summary>
    /// Un callback del hilo de la interfaz lanzó. Con ventana (<paramref name="showError"/>): informe «recuperado», aviso
    /// (una vez por ráfaga) y la app sigue (<c>true</c>). Sin ventana: informe fatal y la excepción sigue su camino. El aviso
    /// es el texto fijo de <see cref="UnexpectedErrorNotice"/>: el mensaje de la excepción solo va al informe.
    /// </summary>
    public static bool OnDispatcherException(Exception exception, Action<string>? showError, ExceptionReport report)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(report);
        if (showError is null)
        {
            _ = report(CrashKind.DispatcherCallback, exception, true, out _);
            return false;
        }

        string? path = report(CrashKind.DispatcherCallback, exception, false, out bool repeated);
        if (!repeated)
        {
            showError(UnexpectedErrorNotice.Describe(path));
        }

        return true;
    }

    /// <summary>
    /// <c>Application.UnhandledException</c> de WinUI. Devuelve si queda atendida: con ventana, informe «recuperado» y
    /// aviso (una vez por ráfaga, con el texto de <see cref="UnexpectedErrorNotice"/>); sin ventana no se atiende (WinUI
    /// cierra la app), pero el informe fatal ya quedó.
    /// </summary>
    public static bool OnXamlUnhandled(Exception? exception, string? message, Action<string>? showError, ExceptionReport report, TextReport reportText)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(reportText);
        bool fatal = showError is null;
        bool repeated;
        string? path = exception is not null
            ? report(CrashKind.XamlUnhandled, exception, fatal, out repeated)
            : reportText(CrashKind.XamlUnhandled, message, fatal, out repeated);

        if (showError is null)
        {
            return false;
        }

        if (!repeated)
        {
            showError(UnexpectedErrorNotice.Describe(path));
        }

        return true;
    }

    /// <summary>
    /// Copia el texto del informe al portapapeles (<paramref name="setClipboardText"/>). Nunca lanza: leer el archivo o el
    /// portapapeles de Windows pueden fallar de muchas maneras (otra app lo tiene abierto, RPC), y entonces lo anota en
    /// <paramref name="log"/> y devuelve <c>false</c> para avisar dónde está el informe.
    /// </summary>
    public static bool TryCopyReport(string path, Action<string> setClipboardText, SessionLog log)
    {
        ArgumentNullException.ThrowIfNull(setClipboardText);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            setClipboardText(File.ReadAllText(path));
            return true;
        }
#pragma warning disable CA1031 // Copiar el informe es un extra: ningún error puede tumbar la app que avisa de otro.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.Append($"No se pudo copiar el informe {path}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// Dónde se enganchan las vías de excepción del proceso (<see cref="CrashRouting.Install"/>). Las pruebas lo cambian
/// por uno que anota el orden sin tocar el proceso de pruebas. Es pública y no sellada a propósito: si fuera interna, el
/// analizador de Rodaje.App (CA1852) pediría sellarla, porque las subclases de las pruebas están en otro ensamblado.
/// </summary>
public class CrashHooks
{
    public virtual void OnUnhandled(UnhandledExceptionEventHandler handler) => AppDomain.CurrentDomain.UnhandledException += handler;

    public virtual void OnUnobservedTask(EventHandler<UnobservedTaskExceptionEventArgs> handler) => TaskScheduler.UnobservedTaskException += handler;

    public virtual void InstallNative(CrashReporter crashes) => NativeCrashHandler.Install(crashes);

    public virtual void AddTraceListener(TraceListener listener) => _ = Trace.Listeners.Add(listener);

    public virtual void OnProcessExit(EventHandler handler) => AppDomain.CurrentDomain.ProcessExit += handler;

    // Observada con UiTask: si la acción lanza, queda informe en vez de perderse en la tarea.
    public virtual void RunLater(TimeSpan delay, Action action) => UiTask.Run(Task.Run(async () =>
    {
        await Task.Delay(delay).ConfigureAwait(false);
        action();
    }));
}
