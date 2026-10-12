using Microsoft.UI.Xaml;
using Rodaje.App.Services;
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
        _ = Task.Run(() =>
        {
            try
            {
                return StreamCopyPlanner.Decide(ExportPlan.Create(request, encoders)).Explanation;
            }
            catch (ExportException)
            {
                return null;
            }
        }).ContinueWith(task =>
        {
            if (_streamCopyKey == key && task.IsCompletedSuccessfully && _model is { } current)
            {
                _streamCopyNote = task.Result;
                ShowAdjustments(current);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ShowAdjustments(ExportDialogModel model)
    {
        IReadOnlyList<string> lines = _streamCopyNote is { } note ? [.. model.Adjustments, note] : model.Adjustments;
        AdjustmentsText.Text = string.Join("\n", lines);
        AdjustmentsText.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
