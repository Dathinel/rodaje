using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export.Running;
using Rodaje.Export.Tests.Acceptance;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Export.Tests.Running;

/// <summary>
/// Recorrido completo del historial: tres ediciones, dos deshechas y una rehecha; tras guardar y reabrir, lo
/// exportado tiene que ser exactamente lo que quedó visible: el corte, el efecto rehecho y la duración del recorte
/// deshecho.
/// </summary>
[Collection(ExportGpuGroup.Name)]
public sealed class EditJourneyHistoryTests(ITestOutputHelper output) : IDisposable
{
    private const int W = 640;
    private const int H = 360;

    public void Dispose() => ExportEndToEndTests.DeleteFolders();

    /// <summary>9: cortar a los 2 s, efecto en la segunda parte, recortarla; deshacer 2 y rehacer 1.</summary>
    [ExportFact]
    public async Task Historial_tras_deshacer_y_rehacer_la_exportacion_refleja_el_estado_visible()
    {
        string folder = ExportEndToEndTests.Folder();
        Project project = EditJourney.NewProject(W, H);
        MediaRef media = EditJourney.Import(project, EditJourney.Media, "a");
        Track track = project.Sequences[0].VideoTracks[0];

        SequenceEditCommand insert = Edits.InsertMedia("sec", media.Id, Time.Zero, EditJourney.S(4), track.Id, Time.Zero, PlacementMode.Overwrite);
        insert.Apply(project);
        var undo = new UndoStack(project);

        // Edición 1: corte a los 2 s. Edición 2: la segunda parte en blanco y negro. Edición 3: recorta 1 s del final.
        SequenceEditCommand split = Edits.Split("sec", insert.CreatedIds[0], EditJourney.S(2));
        undo.Execute(split);
        string secondClip = split.CreatedIds[0];
        undo.Execute(Edits.SetParameterOnClips("sec", [(secondClip, EditJourney.S(3))], BuiltInEffects.Color, BuiltInEffects.ColorParameters.Saturation, -1));
        undo.Execute(Edits.Trim("sec", secondClip, TrimEdge.Out, EditJourney.S(-1)));

        // Se deshacen el recorte y el efecto; se rehace solo el efecto.
        Assert.True(undo.Undo());
        Assert.True(undo.Undo());
        Assert.True(undo.Redo());

        Project loaded = EditJourney.SaveAndReload(project, folder);
        (ExportJob job, string path) = await EditJourney.ExportAsync(output, loaded, Path.Combine(folder, "historial.mp4"));

        // El recorte se deshizo: siguen siendo 4 s (120 cuadros) y el último contenido llega hasta el final.
        Assert.InRange(job.Report!.Video!.FrameCount, 119, 121);
        Assert.Equal(105, EditJourney.Code(path, 105, W, H));

        // La primera mitad sigue en color y la segunda salió en blanco y negro (el efecto rehecho).
        double colored = EditJourney.ChromaDeviation(AcceptanceSupport.DecodeYuv420(path, 30, W, H), W, H);
        double gray = EditJourney.ChromaDeviation(AcceptanceSupport.DecodeYuv420(path, 90, W, H), W, H);
        output.WriteLine($"desviación de croma: antes del corte {colored:0.0}, después {gray:0.0}");
        Assert.True(colored > 12, $"La primera mitad quedó desaturada ({colored:0.0}): el historial se aplicó mal.");
        Assert.True(gray < 6, $"La segunda mitad no quedó en blanco y negro ({gray:0.0}): el efecto rehecho no llegó.");
        Assert.Equal(30, EditJourney.Code(path, 30, W, H));
        Assert.Equal(90, EditJourney.Code(path, 90, W, H));
    }
}
