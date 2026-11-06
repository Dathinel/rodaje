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
/// Recorrido completo del volumen y el fundido de un clip de audio: se edita, se guarda, se reabre, se exporta
/// y se mide el nivel real del archivo: el fundido sube durante su segundo y después queda a los −10 dB pedidos.
/// </summary>
[Collection(ExportGpuGroup.Name)]
public sealed class EditJourneyAudioTests(ITestOutputHelper output) : IDisposable
{
    public void Dispose() => ExportEndToEndTests.DeleteFolders();

    /// <summary>7: −10 dB con fundido de 1 s sobre un clip, con otro sin tocar como referencia de nivel.</summary>
    [ExportFact]
    public async Task Volumen_y_fundido_el_nivel_sube_en_el_fundido_y_queda_a_los_db_pedidos()
    {
        string folder = ExportEndToEndTests.Folder();
        Project project = EditJourney.NewProject(640, 360, audioTrack: true);
        MediaRef media = EditJourney.Import(project, EditJourney.Media, "a");
        Sequence sequence = project.Sequences[0];
        Track sound = sequence.AudioTracks[0];

        // Un primer clip sin tocar (0-2 s) da el nivel de referencia; el segundo lleva −10 dB y fundido de 1 s.
        sequence.VideoTracks[0].Clips.Add(new Clip { Id = "v", MediaId = media.Id, Start = Time.Zero, SourceIn = Time.Zero, SourceOut = EditJourney.S(6) });
        SequenceEditCommand first = Edits.InsertMedia("sec", media.Id, Time.Zero, EditJourney.S(2), sound.Id, Time.Zero, PlacementMode.Overwrite);
        first.Apply(project);
        SequenceEditCommand second = Edits.InsertMedia("sec", media.Id, EditJourney.S(2), EditJourney.S(6), sound.Id, EditJourney.S(2), PlacementMode.Overwrite);
        second.Apply(project);
        string clipId = second.CreatedIds[0];
        Edits.SetParameterOnClips("sec", [(clipId, EditJourney.S(3))], BuiltInEffects.Audio, BuiltInEffects.AudioParameters.GainDb, -10).Apply(project);
        Edits.SetParameterOnClips("sec", [(clipId, EditJourney.S(3))], BuiltInEffects.Audio, BuiltInEffects.AudioParameters.FadeIn, 1).Apply(project);

        Project loaded = EditJourney.SaveAndReload(project, folder);
        (ExportJob job, string path) = await EditJourney.ExportAsync(output, loaded, Path.Combine(folder, "volumen.mp4"), audio: true);
        Assert.InRange(job.Report!.Audio!.DurationSeconds, 5.9, 6.1);

        float[] mono = AcceptanceSupport.DecodeAudioMono(path);

        // Referencia: el tono FSK del segundo 1 (primer clip, sin tocar). Fundido: el del segundo 2 del clip con efecto.
        double reference = EditJourney.Rms(mono, 1.06, 1.50);
        double earlyFade = EditJourney.Rms(mono, 2.06, 2.25);
        double lateFade = EditJourney.Rms(mono, 2.35, 2.50);
        double after = EditJourney.Rms(mono, 3.06, 3.50);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"RMS: referencia {reference:0.000}, fundido {earlyFade:0.000} → {lateFade:0.000}, después {after:0.000}"));

        Assert.True(reference > 0.1, $"El nivel de referencia no es medible ({reference:0.000}).");
        // El fundido sube dentro de su segundo: la segunda mitad suena bastante más que la primera.
        Assert.True(lateFade > earlyFade * 1.4, $"El nivel no sube durante el fundido ({earlyFade:0.000} → {lateFade:0.000}).");
        // Tras el fundido quedan los −10 dB: el nivel es el de referencia por 10^(-10/20) ≈ 0,316.
        Assert.InRange(after / reference, 0.2, 0.45);
    }
}
