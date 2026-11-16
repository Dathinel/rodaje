using Microsoft.UI;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Interchange;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;
using Rodaje.Core.Interchange;
using Rodaje.Core.Model;

namespace Rodaje.App.Views.Interchange;

/// <summary>
/// «Importar línea de tiempo…» y «Exportar línea de tiempo…» (EDL, FCPXML y OpenTimelineIO; D-062) en Archivo. Importar
/// pone el montaje como clip compuesto en pistas nuevas, en un paso del historial, y abre el reenlazado si faltan medios;
/// exportar avisa antes de lo que se pierde y después ofrece el informe completo. La lógica está en
/// <see cref="InterchangeService"/>.
/// </summary>
internal static partial class InterchangeCommands
{
    private const string ImportTitle = "Importar línea de tiempo";
    private const string ExportTitle = "Exportar línea de tiempo";

    private static readonly (string Name, string Spec) All = ("Líneas de tiempo (EDL, FCPXML, OpenTimelineIO)", "*.edl;*.fcpxml;*.otio");
    private static readonly (string Name, string Spec) Otio = ("OpenTimelineIO (*.otio)", "*.otio");
    private static readonly (string Name, string Spec) Fcpxml = ("FCPXML 1.10 (*.fcpxml)", "*.fcpxml");
    private static readonly (string Name, string Spec) Edl = ("EDL CMX 3600 (*.edl)", "*.edl");

    public static IDisposable Bind(ShortcutRouter router, EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(services);
        services.ShortcutRegistry.RegisterRange(InterchangeShortcuts.Catalog);
        IDisposable import = router.Bind(InterchangeShortcuts.Import, () => UiTask.Run(Guard(services, ImportTitle, () => ImportAsync(services))));
        IDisposable export = router.Bind(InterchangeShortcuts.Export, () => UiTask.Run(Guard(services, ExportTitle, () => ExportAsync(services))));
        return new Bindings(import, export);
    }

    private static async Task Guard(EditorServices services, string title, Func<Task> run)
    {
        try
        {
            await run();
        }
#pragma warning disable CA1031 // Un archivo roto o ajeno no debe cerrar la app: se avisa con su mensaje.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            services.Session.Notify(NotificationSeverity.Error, title, ex.Message);
        }
    }

    private static async Task ImportAsync(EditorServices services)
    {
        IReadOnlyList<string> picked = FileDialogs.Open(Owner(services), ImportTitle, multiple: false,
            FileDialogFolderPolicy.OpenFolder(services.Settings.Current), All, FileDialogs.AllFilter);
        FileDialogFolderPolicy.RememberOpen(services.Settings.Current, picked.Count > 0 ? picked[0] : null);
        if (picked.Count == 0)
        {
            return;
        }

        EditorSession session = services.Session;
        Project project = session.Project;
        InterchangeOptions options = InterchangeService.OptionsFor(session.Sequence);
        var service = new InterchangeService(new FfmpegMediaProber());
        InterchangeResult result = await service.ReadAsync(picked[0], project, options);
        if (!ReferenceEquals(project, session.Project))
        {
            return;
        }

        int clips = result.Sequence.AllTracks.Sum(t => t.Clips.Count);
        if (!session.Execute(service.ImportCommand(result, session.SequenceId, session.EditPosition)))
        {
            return;
        }

        string lost = result.Report.IsEmpty ? "" : $" {result.Report.Issues.Count} avisos.";
        Sequence imported = result.Sequence;
        session.Notify(NotificationSeverity.Success, ImportTitle, $"«{imported.Name}»: {clips} clips en un clip compuesto en pistas nuevas.{lost}",
            result.Report.IsEmpty
                ? new NotificationAction("Deshacer", () => session.Undo())
                : new NotificationAction("Ver informe", () => UiTask.Run(InterchangeReportDialog.ShowAsync(services, ImportTitle, result.Report, imported, options))));
        if (InterchangeService.Missing(result).Count > 0)
        {
            await Relink.RelinkDialog.ReopenAsync();
        }
    }

    private static async Task ExportAsync(EditorServices services)
    {
        EditorSession session = services.Session;
        Sequence sequence = session.Sequence;
        if (sequence.Duration.Ticks == 0)
        {
            session.Notify(NotificationSeverity.Informational, ExportTitle, "La secuencia está vacía: no hay nada que exportar.");
            return;
        }

        string suggested = Path.GetFileNameWithoutExtension(session.SuggestedFileName);
        string? path = FileDialogs.Save(Owner(services), ExportTitle, suggested, "otio", services.Settings.Current.LastProjectFolder, Otio, Fcpxml, Edl);
        if (path is null || InterchangeFiles.FormatOf(path) is not { } format)
        {
            return;
        }

        InterchangeOptions options = InterchangeService.OptionsFor(sequence);
        InterchangeReport preview = InterchangeService.Preview(session.Project, sequence.Id, format, options);
        if (preview.Issues.Any(i => i.Severity != IssueSeverity.Info)
            && !await InterchangeReportDialog.ConfirmAsync(services, format, preview))
        {
            return;
        }

        (IReadOnlyList<string> files, InterchangeReport report) = await InterchangeService.ExportAsync(path, session.Project, sequence.Id, options);
        string names = string.Join(", ", files.Select(Path.GetFileName));
        session.Notify(NotificationSeverity.Success, ExportTitle, $"«{sequence.Name}» en {names}.",
            report.IsEmpty ? null : new NotificationAction("Ver informe", () => UiTask.Run(InterchangeReportDialog.ShowAsync(services, ExportTitle, report, sequence, options))));
    }

    internal static nint Owner(EditorServices services) =>
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
