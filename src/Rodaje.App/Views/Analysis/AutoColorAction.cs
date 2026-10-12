using Rodaje.Analysis.Video;
using Rodaje.App.Services;
using Rodaje.App.Services.Format;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Analysis;

/// <summary>
/// «Color automático» y «Balance de blancos automático»: miden el color de unos cuadros del clip y ajustan su efecto de
/// color en un paso del historial; el resultado se ve en el visor y el aviso trae «Deshacer».
/// </summary>
internal static class AutoColorAction
{
    public static async Task RunAsync(EditorServices services, bool levels)
    {
        EditorSession session = services.Session;
        string title = levels ? "Color automático" : "Balance de blancos automático";
        if (ClipVideoSource.SelectedVideoClip(session) is not { } clip)
        {
            session.Notify(NotificationSeverity.Informational, title, "Selecciona un clip de video.");
            return;
        }

        ColorEstimate estimate = await ClipVideoSource.AnalyzeColorAsync(session, clip, CancellationToken.None);
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
