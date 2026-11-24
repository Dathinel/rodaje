using System.Globalization;
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
/// Recorrido completo de la serialización: el mismo proyecto exportado en memoria y tras guardar el .rodaje y
/// reabrirlo del disco tienen que salir iguales cuadro a cuadro y muestra a muestra; una diferencia es una
/// pérdida al serializar.
/// </summary>
[Collection(ExportGpuGroup.Name)]
public sealed class EditJourneySerializationTests(ITestOutputHelper output) : IDisposable
{
    private const int W = 640;
    private const int H = 360;

    public void Dispose() => ExportEndToEndTests.DeleteFolders();

    /// <summary>10: recorte + velocidad + transición + efecto + título + audio, exportado antes y después.</summary>
    [ExportFact]
    public async Task Reabierto_la_exportacion_coincide_con_la_del_proyecto_en_memoria()
    {
        string folder = ExportEndToEndTests.Folder();
        Project project = EditJourney.NewProject(W, H, audioTrack: true);
        MediaRef media = EditJourney.Import(project, EditJourney.Media, "a");
        Sequence sequence = project.Sequences[0];
        Track track = sequence.VideoTracks[0];

        // Un proyecto con varias funciones a la vez: recorte, transición, efecto, título y un clip de audio.
        SequenceEditCommand first = Edits.InsertMedia("sec", media.Id, EditJourney.S(1), EditJourney.S(4), track.Id, Time.Zero, PlacementMode.Overwrite);
        first.Apply(project);
        SequenceEditCommand second = Edits.InsertMedia("sec", media.Id, EditJourney.S(5), EditJourney.S(8), track.Id, EditJourney.S(3), PlacementMode.Overwrite);
        second.Apply(project);
        Edits.AddTransition("sec", first.CreatedIds[0], BuiltInTransitions.Crossfade, EditJourney.S(1)).Apply(project);
        Edits.SetParameterOnClips("sec", [(second.CreatedIds[0], EditJourney.S(6))], BuiltInEffects.Color, BuiltInEffects.ColorParameters.Saturation, -1).Apply(project);
        Edits.InsertTitle("sec", EditJourney.S(1), EditJourney.S(1),
            new ClipGenerator
            {
                Text = "RODAJE",
                Style = new TextStyle
                {
                    Name = "Recorrido",
                    Font = new StyleFont { Family = "Segoe UI", Weight = 800, Fallback = ["Segoe UI"] },
                    Size = 120,
                    Fill = new StyleFill { Color = "#FFE000" },
                },
                Edges = new GeneratorEdges { In = false, Out = false },
            }).Apply(project);
        sequence.AudioTracks[0].Clips.Add(new Clip { Id = "son", MediaId = media.Id, Start = Time.Zero, SourceIn = Time.Zero, SourceOut = EditJourney.S(6) });

        (ExportJob beforeJob, string before) = await EditJourney.ExportAsync(output, project, Path.Combine(folder, "antes.mp4"), audio: true);
        Project loaded = EditJourney.SaveAndReload(project, folder);
        (ExportJob afterJob, string after) = await EditJourney.ExportAsync(output, loaded, Path.Combine(folder, "despues.mp4"), audio: true);

        Assert.Equal(beforeJob.Report!.Video!.FrameCount, afterJob.Report!.Video!.FrameCount);
        (long frames, double worst) = EditJourney.CompareLuma(before, after, W, H);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{frames} cuadros comparados; peor diferencia media {worst:0.000}"));
        Assert.Equal(beforeJob.Report.Video.FrameCount, frames);
        Assert.True(worst < 0.5, $"El archivo reabierto exporta distinto: peor diferencia media {worst:0.000} por cuadro.");

        float[] audioBefore = AcceptanceSupport.DecodeAudioMono(before);
        float[] audioAfter = AcceptanceSupport.DecodeAudioMono(after);
        Assert.Equal(audioBefore.Length, audioAfter.Length);
        double diff = 0;
        for (int i = 0; i < audioBefore.Length; i++)
        {
            diff += Math.Abs(audioBefore[i] - audioAfter[i]);
        }

        diff /= audioBefore.Length;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"diferencia media del audio: {diff:0.00000}"));
        Assert.True(diff < 0.001, $"El audio reabierto exporta distinto: diferencia media {diff:0.00000}.");
    }
}
