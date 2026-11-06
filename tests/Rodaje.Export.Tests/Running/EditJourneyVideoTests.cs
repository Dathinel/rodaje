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
/// Recorridos completos por función de edición de video: se importa el medio, se edita con las órdenes del
/// editor, se guarda el .rodaje, se reabre del disco, se exporta y se comprueba en el archivo que la edición
/// llegó (duración, contenido, mezcla, color, animación y orden de pistas).
/// </summary>
[Collection(ExportGpuGroup.Name)]
public sealed class EditJourneyVideoTests(ITestOutputHelper output) : IDisposable
{
    private const int W = 640;
    private const int H = 360;

    public void Dispose() => ExportEndToEndTests.DeleteFolders();

    /// <summary>1: un clip recortado a [2, 5) del origen exporta 3 s que empiezan por el cuadro 60.</summary>
    [ExportFact]
    public async Task Recorte_la_exportacion_dura_lo_recortado_y_empieza_en_el_cuadro_pedido()
    {
        string folder = ExportEndToEndTests.Folder();
        Project project = EditJourney.NewProject(W, H);
        MediaRef media = EditJourney.Import(project, EditJourney.Media, "a");
        Track track = project.Sequences[0].VideoTracks[0];

        SequenceEditCommand insert = Edits.InsertMedia("sec", media.Id, Time.Zero, EditJourney.S(6), track.Id, Time.Zero, PlacementMode.Overwrite);
        insert.Apply(project);
        string clipId = insert.CreatedIds[0];
        Edits.Trim("sec", clipId, TrimEdge.In, EditJourney.S(2)).Apply(project);
        Edits.Move("sec", clipId, Time.Zero).Apply(project);
        Edits.Trim("sec", clipId, TrimEdge.Out, EditJourney.S(-1)).Apply(project);

        Clip clip = track.Clips[0];
        Assert.Equal((EditJourney.S(0), EditJourney.S(2), EditJourney.S(5)), (clip.Start, clip.SourceIn, clip.SourceOut));

        Project loaded = EditJourney.SaveAndReload(project, folder);
        (ExportJob job, string path) = await EditJourney.ExportAsync(output, loaded, Path.Combine(folder, "recorte.mp4"));

        // 3 s a 30 fps: 90 cuadros, con la tolerancia de uno que pide el recorrido.
        Assert.InRange(job.Report!.Video!.FrameCount, 89, 91);
        Assert.Equal(60, EditJourney.Code(path, 0, W, H));
        Assert.Equal(149, EditJourney.Code(path, 89, W, H));
    }

    /// <summary>2: al 200 % el clip dura la mitad y las marcas de audio siguen en su sitio hasta el final.</summary>
    [ExportFact]
    public async Task Velocidad_al_200_dura_la_mitad_y_el_audio_sigue_sincronizado_al_final()
    {
        string folder = ExportEndToEndTests.Folder();
        Project project = EditJourney.NewProject(W, H, audioTrack: true);
        MediaRef media = EditJourney.Import(project, EditJourney.Media, "a");
        Sequence sequence = project.Sequences[0];

        // Video y sonido del mismo medio, enlazados, del segundo 1 al 7 del origen: 6 s que al doble ocupan 3.
        var video = new Clip { Id = "v", MediaId = media.Id, Start = Time.Zero, SourceIn = EditJourney.S(1), SourceOut = EditJourney.S(7), LinkId = "l" };
        var sound = new Clip { Id = "son", MediaId = media.Id, Start = Time.Zero, SourceIn = EditJourney.S(1), SourceOut = EditJourney.S(7), LinkId = "l" };
        sequence.VideoTracks[0].Clips.Add(video);
        sequence.AudioTracks[0].Clips.Add(sound);
        Edits.SetSpeedOnClips("sec", ["v", "son"], new Rational(2, 1)).Apply(project);
        Assert.Equal(EditJourney.S(3), video.Duration);

        Project loaded = EditJourney.SaveAndReload(project, folder);
        (ExportJob job, string path) = await EditJourney.ExportAsync(output, loaded, Path.Combine(folder, "velocidad.mp4"), audio: true);

        Assert.InRange(job.Report!.Video!.FrameCount, 89, 91);
        Assert.Equal(30, EditJourney.Code(path, 0, W, H));
        Assert.InRange(EditJourney.Code(path, 45, W, H) ?? -1, 118, 122);

        // Las marcas de los segundos 1 a 6 del origen tienen que salir cada medio segundo.
        float[] mono = AcceptanceSupport.DecodeAudioMono(path);
        float[] chirp = RetimedAudioCode.ChirpTemplate(48000, 2.0, preservePitch: true);
        foreach (int second in Enumerable.Range(1, 6))
        {
            int expected = (second - 1) * 24000;
            AudioMark? found = RetimedAudioCode.FindChirp(mono, chirp, expected - 7200, expected + 7200, minCorrelation: 0.4);
            Assert.True(found.HasValue, $"La marca del segundo {second} del origen (≈{(second - 1) / 2.0:0.0} s) no suena en la exportación.");
            AudioMark mark = found.Value;
            double errorMs = (mark.Sample - expected) * 1000.0 / 48000;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"marca del segundo {second}: muestra {mark.Sample} ({errorMs:+0;-0;0} ms), correlación {mark.Correlation:0.00}"));
            Assert.True(Math.Abs(errorMs) <= 100, $"La marca del segundo {second} llegó con {errorMs:0} ms de error: el audio no sigue al video al final.");
        }
    }

    /// <summary>3: el fundido cruzado de 1 s mezcla en el medio y mantiene los 6 s de los dos clips.</summary>
    [ExportFact]
    public async Task Transicion_el_fundido_mezcla_en_el_corte_y_la_duracion_suma()
    {
        string folder = ExportEndToEndTests.Folder();
        Project project = EditJourney.NewProject(W, H);
        MediaRef media = EditJourney.Import(project, EditJourney.Media, "a");
        Track track = project.Sequences[0].VideoTracks[0];

        // Dos tramos de 3 s con material de sobra a ambos lados del corte, fundido de 1 s centrado.
        SequenceEditCommand first = Edits.InsertMedia("sec", media.Id, EditJourney.S(1), EditJourney.S(4), track.Id, Time.Zero, PlacementMode.Overwrite);
        first.Apply(project);
        SequenceEditCommand second = Edits.InsertMedia("sec", media.Id, EditJourney.S(5), EditJourney.S(8), track.Id, EditJourney.S(3), PlacementMode.Overwrite);
        second.Apply(project);
        Edits.AddTransition("sec", first.CreatedIds[0], BuiltInTransitions.Crossfade, EditJourney.S(1)).Apply(project);

        Project loaded = EditJourney.SaveAndReload(project, folder);
        (ExportJob job, string path) = await EditJourney.ExportAsync(output, loaded, Path.Combine(folder, "transicion.mp4"));

        // La transición va dentro de los clips: el total sigue siendo 6 s (180 cuadros).
        Assert.InRange(job.Report!.Video!.FrameCount, 179, 181);

        // Antes y después del fundido se leen limpios los números de cada lado.
        Assert.Equal(90, EditJourney.Code(path, 60, W, H));
        Assert.Equal(195, EditJourney.Code(path, 135, W, H));

        // En el centro del fundido (t = 3 s) los dos códigos se mezclan y ya no se lee ninguno limpio.
        Assert.Null(EditJourney.Code(path, 90, W, H));

        // Y el cuadro mezclado coincide con lo que el propio editor compone para ese instante.
        double psnr = EditJourney.PsnrAgainstPreview(loaded, path, 60, 90, 135);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"PSNR contra la vista previa: peor {psnr:0.0} dB"));
        Assert.True(psnr >= 35, $"El fundido no llegó a la exportación igual que en la vista previa (PSNR {psnr:0.0} dB).");
    }

    /// <summary>4: el título se ve en su intervalo y fuera de él no queda nada.</summary>
    [ExportFact]
    public async Task Titulo_aparece_en_su_intervalo_y_desaparece_fuera()
    {
        string folder = ExportEndToEndTests.Folder();
        Project project = EditJourney.NewProject(W, H);
        MediaRef media = EditJourney.Import(project, EditJourney.Media, "a");
        Track track = project.Sequences[0].VideoTracks[0];
        Edits.InsertMedia("sec", media.Id, Time.Zero, EditJourney.S(3.5), track.Id, Time.Zero, PlacementMode.Overwrite).Apply(project);

        var style = new TextStyle
        {
            Name = "Recorrido",
            Font = new StyleFont { Family = "Segoe UI", Weight = 800, Fallback = ["Segoe UI"] },
            Size = 160,
            Fill = new StyleFill { Color = "#FF00FF" },
        };
        var generator = new ClipGenerator { Text = "RODAJE", Style = style, Edges = new GeneratorEdges { In = false, Out = false } };
        Edits.InsertTitle("sec", EditJourney.S(1), EditJourney.S(1.5), generator).Apply(project);

        Project loaded = EditJourney.SaveAndReload(project, folder);
        (ExportJob _, string path) = await EditJourney.ExportAsync(output, loaded, Path.Combine(folder, "titulo.mp4"));

        foreach (long frame in (long[])[15, 45, 60, 96])
        {
            int magenta = EditJourney.TitlePixels(EditJourney.DecodeRgb(path, frame, W, H));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"cuadro {frame}: {magenta} px del título"));
            if (frame is 45 or 60)
            {
                Assert.True(magenta > 400, $"En el cuadro {frame}, dentro del intervalo del título, solo hay {magenta} px del título.");
            }
            else
            {
                Assert.True(magenta < 20, $"En el cuadro {frame}, fuera del intervalo, quedan {magenta} px del título.");
            }
        }

        // El video de abajo sigue igual: su número de cuadro se lee fuera del intervalo del título.
        Assert.Equal(15, EditJourney.Code(path, 15, W, H));
        Assert.Equal(96, EditJourney.Code(path, 96, W, H));
    }

    /// <summary>5: saturación −1 deja el clip en blanco y negro y no toca a su vecino.</summary>
    [ExportFact]
    public async Task Efecto_la_desaturacion_se_ve_en_su_clip_y_no_en_el_vecino()
    {
        string folder = ExportEndToEndTests.Folder();
        Project project = EditJourney.NewProject(W, H);
        MediaRef media = EditJourney.Import(project, EditJourney.Media, "a");
        Track track = project.Sequences[0].VideoTracks[0];

        Edits.InsertMedia("sec", media.Id, Time.Zero, EditJourney.S(1.5), track.Id, Time.Zero, PlacementMode.Overwrite).Apply(project);
        SequenceEditCommand second = Edits.InsertMedia("sec", media.Id, EditJourney.S(2), EditJourney.S(3.5), track.Id, EditJourney.S(1.5), PlacementMode.Overwrite);
        second.Apply(project);
        Edits.SetParameterOnClips("sec", [(second.CreatedIds[0], EditJourney.S(2.5))], BuiltInEffects.Color, BuiltInEffects.ColorParameters.Saturation, -1).Apply(project);

        Project loaded = EditJourney.SaveAndReload(project, folder);
        (ExportJob _, string path) = await EditJourney.ExportAsync(output, loaded, Path.Combine(folder, "efecto.mp4"));

        // En el clip sin tocar el croma se aleja del neutro; en el desaturado queda pegado a 128.
        double colored = EditJourney.ChromaDeviation(AcceptanceSupport.DecodeYuv420(path, 20, W, H), W, H);
        double gray = EditJourney.ChromaDeviation(AcceptanceSupport.DecodeYuv420(path, 75, W, H), W, H);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"desviación de croma: con color {colored:0.0}, en blanco y negro {gray:0.0}"));
        Assert.True(colored > 12, $"El clip sin efecto también salió desaturado ({colored:0.0}).");
        Assert.True(gray < 6, $"El clip con saturación −1 conserva croma ({gray:0.0}).");
        Assert.Equal(90, EditJourney.Code(path, 75, W, H));
    }

    /// <summary>6: exposición animada de −3 a +3: oscuro al principio, normal en medio y quemado al final.</summary>
    [ExportFact]
    public async Task Keyframes_el_parametro_animado_interpola_en_la_exportacion()
    {
        string folder = ExportEndToEndTests.Folder();
        Project project = EditJourney.NewProject(W, H);
        MediaRef media = EditJourney.Import(project, EditJourney.Media, "a");
        Track track = project.Sequences[0].VideoTracks[0];

        SequenceEditCommand insert = Edits.InsertMedia("sec", media.Id, Time.Zero, EditJourney.S(2), track.Id, Time.Zero, PlacementMode.Overwrite);
        insert.Apply(project);
        string clipId = insert.CreatedIds[0];
        SequenceEditCommand effect = Edits.EnsureEffect("sec", clipId, BuiltInEffects.Color);
        effect.Apply(project);
        string effectId = effect.CreatedIds[0];
        Edits.SetKeyframe("sec", clipId, effectId, BuiltInEffects.ColorParameters.Exposure, Time.Zero, -3, Interpolation.Linear).Apply(project);
        Edits.SetKeyframe("sec", clipId, effectId, BuiltInEffects.ColorParameters.Exposure, EditJourney.S(1.9), 3, Interpolation.Linear).Apply(project);

        Project loaded = EditJourney.SaveAndReload(project, folder);
        (ExportJob _, string path) = await EditJourney.ExportAsync(output, loaded, Path.Combine(folder, "keyframes.mp4"));

        double start = EditJourney.MeanLuma(EditJourney.Luma(path, 0, W, H));
        double middle = EditJourney.MeanLuma(EditJourney.Luma(path, 30, W, H));
        double end = EditJourney.MeanLuma(EditJourney.Luma(path, 59, W, H));
        // El mismo cuadro del medio sin efecto, leído del medio original: en el centro la interpolación vale 0.
        double plain = EditJourney.MeanLuma(EditJourney.Luma(media.Path, 30, W, H));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"luma media: principio {start:0.0}, medio {middle:0.0} (origen {plain:0.0}), final {end:0.0}"));

        Assert.True(start + 15 < middle, $"El primer keyframe no se aplicó: principio {start:0.0}, medio {middle:0.0}.");
        Assert.True(middle + 15 < end, $"El último keyframe no se aplicó: medio {middle:0.0}, final {end:0.0}.");
        Assert.True(Math.Abs(middle - plain) < 15, $"En medio la exposición no vale 0: {middle:0.0} frente a {plain:0.0} del medio.");
    }

    /// <summary>8: dos pistas, la de arriba recortada y semitransparente en un cuadrante; se respeta el orden.</summary>
    [ExportFact]
    public async Task Varias_pistas_la_de_arriba_compone_encima_y_solo_en_su_intervalo()
    {
        string folder = ExportEndToEndTests.Folder();
        Project project = EditJourney.NewProject(W, H);
        MediaRef below = EditJourney.Import(project, EditJourney.Media, "a");
        MediaRef above = EditJourney.Import(project, EditJourney.MediaSmall, "b");
        Sequence sequence = project.Sequences[0];
        Track track = sequence.VideoTracks[0];
        var upper = new Track { Id = "v1", Kind = TrackKind.Video, Name = "Video 2" };
        sequence.VideoTracks.Add(upper);

        Edits.InsertMedia("sec", below.Id, Time.Zero, EditJourney.S(4), track.Id, Time.Zero, PlacementMode.Overwrite).Apply(project);
        SequenceEditCommand pip = Edits.InsertMedia("sec", above.Id, EditJourney.S(3), EditJourney.S(4.5), upper.Id, EditJourney.S(1), PlacementMode.Overwrite);
        pip.Apply(project);
        string pipId = pip.CreatedIds[0];
        Edits.SetParameterOnClips("sec", [(pipId, EditJourney.S(3.5))], BuiltInEffects.Transform, BuiltInEffects.TransformParameters.Scale, 0.5).Apply(project);
        Edits.SetParameterOnClips("sec", [(pipId, EditJourney.S(3.5))], BuiltInEffects.Transform, BuiltInEffects.TransformParameters.X, 160).Apply(project);
        Edits.SetParameterOnClips("sec", [(pipId, EditJourney.S(3.5))], BuiltInEffects.Transform, BuiltInEffects.TransformParameters.Y, 90).Apply(project);
        Edits.SetParameterOnClips("sec", [(pipId, EditJourney.S(3.5))], BuiltInEffects.Transform, BuiltInEffects.TransformParameters.Opacity, 0.5).Apply(project);

        Project loaded = EditJourney.SaveAndReload(project, folder);
        (ExportJob job, string path) = await EditJourney.ExportAsync(output, loaded, Path.Combine(folder, "pistas.mp4"));

        // 4 s a 30 fps: 120 cuadros. Fuera del intervalo de la PiP se lee limpio el número de la pista de abajo.
        Assert.InRange(job.Report!.Video!.FrameCount, 119, 121);
        Assert.Equal(15, EditJourney.Code(path, 15, W, H));
        Assert.Equal(75, EditJourney.Code(path, 75, W, H));

        // Dentro del intervalo: el número de la pista de abajo se sigue leyendo (la PiP no cubre su esquina)…
        byte[] luma45 = EditJourney.Luma(path, 45, W, H);
        Assert.Equal(45, FrameCode.Read(luma45, W, H, W));
        // …y en el cuadrante de la PiP lo único legible, si algo se lee, es el número de la pista de arriba
        // (105 = el cuadro 3,5 s de su medio en ese instante); el de abajo queda mezclado.
        int? aboveCode = EditJourney.RegionCode(luma45, W, 320, 180, 320, 180);
        Assert.True(aboveCode is null or 105, $"En el cuadrante de la pista de arriba se leyó {aboveCode}: ni el cuadro de arriba ni la mezcla.");
        double change = EditJourney.RegionDiff(luma45, EditJourney.Luma(path, 15, W, H), W, 340, 200, 280, 140);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"diferencia media en el cuadrante de la PiP: {change:0.0}"));
        Assert.True(change > 8, $"En el cuadrante de la pista de arriba no cambió nada ({change:0.0}): la PiP no llegó a la exportación.");

        // La composición coincide con lo que el editor muestra en la vista previa en esos mismos instantes.
        double psnr = EditJourney.PsnrAgainstPreview(loaded, path, 15, 45, 75);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"PSNR contra la vista previa: peor {psnr:0.0} dB"));
        Assert.True(psnr >= 35, $"La composición de pistas no llegó igual a la exportación (PSNR {psnr:0.0} dB).");
    }
}
