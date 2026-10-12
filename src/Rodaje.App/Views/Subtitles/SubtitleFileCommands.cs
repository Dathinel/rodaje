using Microsoft.UI;
using Rodaje.App.Services;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Views.Subtitles;

/// <summary>
/// «Importar subtítulos...» y «Exportar subtítulos...» (SRT y WebVTT), en Archivo y en el menú de la cabecera de una
/// pista de subtítulos. Sin atajo por omisión.
/// </summary>
internal static partial class SubtitleFileCommands
{
    public const string ImportId = "file.importSubtitles";
    public const string ExportId = "file.exportSubtitles";
    public const string ImportLabel = "Importar subtítulos...";
    public const string ExportLabel = "Exportar subtítulos...";

    private static readonly (string Name, string Spec) Both = ("Subtítulos (SRT, WebVTT)", "*.srt;*.vtt");
    private static readonly (string Name, string Spec) Srt = ("SubRip (*.srt)", "*.srt");
    private static readonly (string Name, string Spec) Vtt = ("WebVTT (*.vtt)", "*.vtt");

    public static IDisposable Bind(ShortcutRouter router, EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(services);
        services.ShortcutRegistry.Register(new ShortcutAction(ImportId, "Importar subtítulos", ShortcutGroups.File, [])
        {
            Description = "Trae un archivo .srt o .vtt a una pista de subtítulos nueva.",
        });
        services.ShortcutRegistry.Register(new ShortcutAction(ExportId, "Exportar subtítulos", ShortcutGroups.File, [])
        {
            Description = "Guarda la pista de subtítulos como archivo .srt o .vtt.",
        });
        IDisposable import = router.Bind(ImportId, () => _ = Guard(services, "Importar subtítulos", () => ImportAsync(services)));
        IDisposable export = router.Bind(ExportId, () => _ = Guard(services, "Exportar subtítulos", () => ExportAsync(services, null)));
        return new Bindings(import, export);
    }

    /// <summary>Exportar una pista concreta (menú de su cabecera).</summary>
    public static void ExportTrack(EditorServices services, string trackId) =>
        _ = Guard(services, "Exportar subtítulos", () => ExportAsync(services, trackId));

    private static async Task Guard(EditorServices services, string title, Func<Task> run)
    {
        try
        {
            await run();
        }
#pragma warning disable CA1031 // Un archivo ilegible o una carpeta sin permiso no deben cerrar la app: se avisa.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            services.Session.Notify(NotificationSeverity.Error, title, ex.Message);
        }
    }

    private static async Task ImportAsync(EditorServices services)
    {
        IReadOnlyList<string> picked = FileDialogs.Open(Owner(services), "Importar subtítulos", multiple: false,
            services.Settings.Current.LastMediaFolder, Both, FileDialogs.AllFilter);
        if (picked.Count == 0)
        {
            return;
        }

        string path = picked[0];
        SubtitleFileContent content = await Task.Run(() => SubtitleFiles.Read(path));
        EditorSession session = services.Session;
        if (content.Cues.Count == 0)
        {
            session.Notify(NotificationSeverity.Warning, "Importar subtítulos", $"«{Path.GetFileName(path)}» no trae subtítulos que se puedan leer.");
            return;
        }

        int count = session.ImportSubtitles(content.Cues, Path.GetFileNameWithoutExtension(path));
        if (count > 0)
        {
            string skipped = content.Skipped > 0 ? $" {content.Skipped} bloques no se entendieron y se saltaron." : "";
            session.Notify(NotificationSeverity.Success, "Importar subtítulos",
                $"{count} subtítulos en una pista nueva.{skipped}", new NotificationAction("Deshacer", () => session.Undo()));
        }
    }

    private static Task ExportAsync(EditorServices services, string? trackId)
    {
        EditorSession session = services.Session;
        if (session.SubtitleTrackToExport(trackId) is not { } track)
        {
            session.Notify(NotificationSeverity.Informational, "Exportar subtítulos", "No hay subtítulos que exportar: crea o importa antes una pista de subtítulos.");
            return Task.CompletedTask;
        }

        IReadOnlyList<SubtitleCue> cues = SubtitleEdits.CuesOf(track);
        string suggested = SubtitleFiles.SuggestedFileName(session.ProjectName, track.Name);
        string? path = FileDialogs.Save(Owner(services), "Exportar subtítulos", suggested, "srt",
            services.Settings.Current.LastProjectFolder, Srt, Vtt);
        if (path is null)
        {
            return Task.CompletedTask;
        }

        SubtitleFiles.Write(path, cues);
        session.Notify(NotificationSeverity.Success, "Exportar subtítulos", $"{cues.Count} subtítulos de «{track.Name}» en {Path.GetFileName(path)}.");
        return Task.CompletedTask;
    }

    private static nint Owner(EditorServices services) =>
        services.Shell.XamlRoot?.ContentIslandEnvironment is { } environment ? Win32Interop.GetWindowFromWindowId(environment.AppWindowId) : 0;

    private sealed partial class Bindings(IDisposable a, IDisposable b) : IDisposable
    {
        public void Dispose()
        {
            a.Dispose();
            b.Dispose();
        }
    }
}
