using Rodaje.Analysis.Video;
using Rodaje.App.Services;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.LongActions;
using Rodaje.Media;

namespace Rodaje.App.Views.Analysis;

/// <summary>
/// «Color automático» y «Balance de blancos automático»: miden el color de unos cuadros del clip y ajustan su efecto de
/// color en un paso del historial; el resultado se ve en el visor y el aviso trae «Deshacer». Si no se puede medir (el
/// archivo del medio falta o no se lee), avisa con el texto del error, ya escrito para el usuario, como «Seguir»: el aviso
/// genérico de <see cref="LongActionService"/> queda para los fallos inesperados.
/// </summary>
internal static class AutoColorAction
{
    public static async Task RunAsync(EditorSession session, bool levels, LongActionScope scope)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(scope);
        string title = levels ? "Color automático" : "Balance de blancos automático";
        if (ClipVideoSource.SelectedVideoClip(session) is not { } clip)
        {
            session.Notify(NotificationSeverity.Informational, title, "Selecciona un clip de video.");
            return;
        }

        scope.Report(0, "Midiendo el color…");
        ColorEstimate estimate;
        try
        {
            estimate = await ClipVideoSource.AnalyzeColorAsync(session, clip, scope.Progress, scope.Token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or MediaException)
        {
            session.Notify(NotificationSeverity.Warning, title, ex.Message);
            return;
        }

        if (session.Sequence.FindClip(clip.Id) is null)
        {
            return;
        }

        ColorSettings settings = levels ? AutoColor.Auto(estimate) : AutoColor.WhiteBalance(estimate);
        if (!session.Execute(VideoAnalysisEdits.Color(session.SequenceId, clip.Id, settings, levels)))
        {
            return;
        }

        string message = string.Create(SpanishNumbers.Format,
            $"Temperatura {settings.Temperature * 100:+0;-0;0} %, matiz {settings.Tint * 100:+0;-0;0} %, exposición {settings.Exposure:+0.00;-0.00;0} EV")
            + (levels ? string.Create(SpanishNumbers.Format, $", contraste {settings.Contrast * 100:+0;-0;0} %.") : ".");
        session.Notify(NotificationSeverity.Success, title, message, new NotificationAction("Deshacer", () => session.Undo()));
    }
}
