using Microsoft.UI.Xaml;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.Export;
using Rodaje.Export.Running;
using Rodaje.Media.Export;

namespace Rodaje.App.Views.Export;

/// <summary>
/// La decisión de copia directa («smart render») explicada en el lado del diálogo: se calcula fuera del hilo de la interfaz
/// (lee los keyframes del original) y solo cuando cambian los ajustes o el rango.
/// </summary>
internal sealed partial class ExportView
{
    private string? _streamCopyKey;
    private string? _streamCopyNote;

    private void RefreshStreamCopy(ExportDialogModel model)
    {
        if (_jobs.Runner.IsSimulation || model.Settings.Video is null || !model.CanExport)
        {
            _streamCopyKey = null;
            _streamCopyNote = null;
            return;
        }

        ExportSettings settings = model.Settings with { Performance = model.Performance };
        if (settings.Video is { } video && model.VideoEncoder is { } encoder)
        {
            settings = settings with { Video = video with { EncoderId = encoder.Id } };
        }

        ExportRange? range = model.Ranges[model.RangeIndex].Range;
        string key = $"{settings}|{range}";
        if (key == _streamCopyKey)
        {
            return;
        }

        _streamCopyKey = key;
        _streamCopyNote = null;
        EditorSession session = _services.Session;
        string? folder = session.FilePath is { } file ? Path.GetDirectoryName(file) : null;
        var request = new ExportRequest(session.Project.Clone(), session.SequenceId, model.OutputPath, settings, range) { ProjectDirectory = folder };
        IReadOnlyList<EncoderSpec> encoders = model.Encoders;
        // Con await y no con ContinueWith: un fallo inesperado de la decisión ya no queda en una tarea que nadie mira (la
        // continuación solo miraba si había terminado bien). Se informa sin aviso: la nota es solo una explicación.
        UiTask.Run(async () =>
        {
            string? note = await Task.Run(() => Explain(request, encoders)).ConfigureAwait(true);
            if (_streamCopyKey == key && _model is { } current)
            {
                _streamCopyNote = note;
                ShowAdjustments(current);
            }
        });
    }

    private static string? Explain(ExportRequest request, IReadOnlyList<EncoderSpec> encoders)
    {
        try
        {
            return StreamCopyPlanner.Decide(ExportPlan.Create(request, encoders)).Explanation;
        }
        catch (ExportException)
        {
            return null;
        }
#pragma warning disable CA1031 // Cualquier otro fallo se informa y el diálogo sigue sin la nota.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UiTask.Report(ex, notify: false);
            return null;
        }
    }

    private void ShowAdjustments(ExportDialogModel model)
    {
        IReadOnlyList<string> lines = _streamCopyNote is { } note ? [.. model.Adjustments, note] : model.Adjustments;
        AdjustmentsText.Text = string.Join("\n", lines);
        AdjustmentsText.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
