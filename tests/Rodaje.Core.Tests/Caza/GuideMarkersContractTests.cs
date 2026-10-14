using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Commands;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Commands.CommandFixtures;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Lo que prometen «Marcadores y regiones» y «Clips compuestos» de <c>docs/guia/edicion.md</c> y los subtítulos de
/// <c>docs/guia/titulos-y-subtitulos.md</c>, comprobado en los comandos de Core. Cada prueba cita la frase.
/// </summary>
public sealed class GuideMarkersContractTests
{
    private static (Project Project, UndoStack Stack) New()
    {
        Project project = Create();
        return (project, new UndoStack(project));
    }

    private static GuidIdGenerator Ids() => GuidIdGenerator.Instance;

    // «M deja un marcador en el cursor… arrastrarlo lo mueve y clic derecho lo borra.» Cada cambio es un paso.
    [Fact]
    public void Marcador_anadir_mover_y_quitar_son_un_paso_cada_uno()
    {
        (Project project, UndoStack stack) = New();
        string before = ProjectDump.Of(project);
        var add = Edits.AddMarker(SequenceId, S(2), "Marcador 1", Ids());
        stack.Execute(add);
        string id = add.CreatedIds[0];
        string added = ProjectDump.Of(project);
        stack.Execute(Edits.MoveMarker(SequenceId, id, S(4)));
        Assert.Equal(S(4), Seq(project).Markers.Single().Time);
        stack.BreakMerge();
        stack.Execute(Edits.RemoveMarker(SequenceId, id));
        Assert.Empty(Seq(project).Markers);

        Assert.Equal(3, stack.Count);
        stack.Undo();
        stack.Undo();
        Assert.Equal(added, ProjectDump.Of(project));
        stack.Undo();
        Assert.Equal(before, ProjectDump.Of(project));
    }

    // «La categoría Marcadores … lista marcadores y regiones en orden de tiempo.»
    [Fact]
    public void Marcadores_y_regiones_quedan_en_orden_de_tiempo_al_anadir_mover_y_editar()
    {
        (Project project, UndoStack stack) = New();
        var ids = Ids();
        stack.Execute(Edits.AddMarker(SequenceId, S(5), "A", ids));
        stack.Execute(Edits.AddMarker(SequenceId, S(1), "B", ids));
        stack.Execute(Edits.AddMarker(SequenceId, S(3), "C", ids));
        Assert.Equal(["B", "C", "A"], Seq(project).Markers.Select(m => m.Name));

        string a = Seq(project).Markers.Single(m => m.Name == "A").Id;
        stack.Execute(Edits.MoveMarker(SequenceId, a, S(0)));
        Assert.Equal(["A", "B", "C"], Seq(project).Markers.Select(m => m.Name));

        stack.Execute(Edits.AddRegion(SequenceId, S(6), S(8), "R1", ids));
        var second = Edits.AddRegion(SequenceId, S(2), S(3), "R2", ids);
        stack.Execute(second);
        Assert.Equal(["R2", "R1"], Seq(project).Regions.Select(r => r.Name));

        stack.Execute(Edits.EditRegion(SequenceId, second.CreatedIds[0], "R2", null, null, S(9), S(10)));
        Assert.Equal(["R1", "R2"], Seq(project).Regions.Select(r => r.Name));
    }

    // «F2 o el lápiz para editar (nombre, color y nota) … el de siempre … Cada cambio es un paso del historial.»
    [Fact]
    public void Editar_nombre_color_y_nota_es_un_paso_y_el_de_siempre_es_sin_color()
    {
        (Project project, UndoStack stack) = New();
        var add = Edits.AddMarker(SequenceId, S(2), "Marcador 1", Ids());
        stack.Execute(add);
        string id = add.CreatedIds[0];
        string plain = ProjectDump.Of(project);

        stack.Execute(Edits.EditMarker(SequenceId, id, "Entrada", "#ff0000", "Revisar el audio"));
        Marker marker = Seq(project).Markers.Single();
        Assert.Equal(("Entrada", "#FF0000", "Revisar el audio"), (marker.Name, marker.Color, marker.Note));

        stack.Execute(Edits.EditMarker(SequenceId, id, "Entrada", null, null));
        marker = Seq(project).Markers.Single();
        Assert.Null(marker.Color);
        Assert.Null(marker.Note);

        Assert.Equal(3, stack.Count);
        stack.Undo();
        Assert.Equal("#FF0000", Seq(project).Markers.Single().Color);
        stack.Undo();
        Assert.Equal(plain, ProjectDump.Of(project));
    }

    // Lo mismo para la región: nombre, color, nota y tramo en un paso.
    [Fact]
    public void Editar_region_es_un_paso_y_deshacer_la_devuelve_igual()
    {
        (Project project, UndoStack stack) = New();
        var add = Edits.AddRegion(SequenceId, S(1), S(3), "Región 1", Ids());
        stack.Execute(add);
        string plain = ProjectDump.Of(project);

        stack.Execute(Edits.EditRegion(SequenceId, add.CreatedIds[0], "Intro", "#00AA55", "nota", S(0), S(4)));
        Region region = Seq(project).Regions.Single();
        Assert.Equal(("Intro", "#00AA55", "nota", S(0), S(4)), (region.Name, region.Color, region.Note, region.Start, region.End));

        stack.Undo();
        Assert.Equal(plain, ProjectDump.Of(project));
        Region back = Seq(project).Regions.Single();
        Assert.Equal(("Región 1", null, null), (back.Name, back.Color, back.Note));
    }

    // «Supr o la papelera para quitar»: también las regiones, y deshacer las devuelve con su color y su nota.
    [Fact]
    public void Quitar_region_con_color_y_nota_se_deshace_entera()
    {
        (Project project, UndoStack stack) = New();
        var add = Edits.AddRegion(SequenceId, S(1), S(3), "Región 1", Ids());
        stack.Execute(add);
        stack.Execute(Edits.EditRegion(SequenceId, add.CreatedIds[0], "Intro", "#123456", "nota", S(1), S(3)));
        string edited = ProjectDump.Of(project);

        stack.Execute(Edits.RemoveRegion(SequenceId, add.CreatedIds[0]));
        Assert.Empty(Seq(project).Regions);
        stack.Undo();
        Assert.Equal(edited, ProjectDump.Of(project));
        Assert.Equal(("#123456", "nota"), (Seq(project).Regions.Single().Color, Seq(project).Regions.Single().Note));
    }

    // «Detectar ritmo deja un marcador «Pulso» en cada tiempo de la música», en un paso.
    [Fact]
    public void Detectar_ritmo_deja_un_Pulso_por_tiempo_en_un_paso()
    {
        (Project project, UndoStack stack) = New();
        stack.Execute(Edits.AddBeatMarkers(SequenceId, [S(3), S(1), S(2)], ids: Ids()));
        Assert.Equal([S(1), S(2), S(3)], Seq(project).Markers.Select(m => m.Time));
        Assert.All(Seq(project).Markers, m => Assert.Equal("Pulso", m.Name));
        Assert.Equal(1, stack.Count);
        stack.Undo();
        Assert.Empty(Seq(project).Markers);
    }

    // «Alt+G (Crear clip compuesto): quedan como uno solo … Alt+Mayús+G descompone.» Cada uno es un paso.
    [Fact]
    public void Compuesto_crear_y_descomponer_vuelve_a_los_mismos_clips()
    {
        (Project project, UndoStack stack) = New();
        string before = ProjectDump.Of(project);
        var create = Edits.CreateCompound(SequenceId, ["clip-1"], "Compuesto 1", Ids());
        stack.Execute(create);
        string compound = create.CreatedIds[0];
        string nested = create.CreatedIds[^1];
        Assert.DoesNotContain(Seq(project).VideoTracks[0].Clips, c => c.Id == "clip-1");
        Assert.True(Seq(project).FindClip(compound)!.Value.Clip.IsCompound);
        Assert.NotNull(project.FindSequence(nested));

        var decompose = Edits.DecomposeCompound(SequenceId, compound, Ids());
        stack.Execute(decompose);
        Assert.Contains("clip-1", decompose.CreatedIds);
        Assert.Contains(Seq(project).VideoTracks[0].Clips, c => c.Id == "clip-1" && c.Start == S(0));
        Assert.Null(project.FindSequence(nested));

        stack.Undo();
        Assert.NotNull(project.FindSequence(nested));
        stack.Undo();
        Assert.Equal(before, ProjectDump.Of(project));
        stack.Redo();
        stack.Redo();
        Assert.Contains(Seq(project).VideoTracks[0].Clips, c => c.Id == "clip-1");
    }

    // «Doble clic entra a editar su secuencia»: lo editado dentro se deshace y rehace aunque se haya deshecho el compuesto.
    [Fact]
    public void Editar_dentro_del_compuesto_y_deshacer_todo_devuelve_el_proyecto()
    {
        (Project project, UndoStack stack) = New();
        string before = ProjectDump.Of(project);
        var ids = Ids();
        var create = Edits.CreateCompound(SequenceId, ["clip-1"], "Compuesto 1", ids);
        stack.Execute(create);
        string nested = create.CreatedIds[^1];
        stack.Execute(Edits.AddMarker(nested, S(1), "Dentro", ids));
        stack.Execute(Edits.AddEffect(nested, "clip-1", BuiltInEffects.Transform, ids: ids));
        string inside = ProjectDump.Of(project);

        stack.Undo();
        stack.Undo();
        stack.Undo();
        Assert.Equal(before, ProjectDump.Of(project));
        stack.Redo();
        stack.Redo();
        stack.Redo();
        Assert.Equal(inside, ProjectDump.Of(project));
    }

    // «El compuesto lleva sus propios efectos, transformación, opacidad, máscaras, mezcla y transiciones, como cualquier clip.»
    [Fact]
    public void El_compuesto_lleva_efectos_mezcla_y_transiciones_como_cualquier_clip()
    {
        (Project project, UndoStack stack) = New();
        var ids = Ids();
        Seq(project).GetTrack("track-v1").Clips[1].Start = S(5);
        var create = Edits.CreateCompound(SequenceId, ["clip-1"], "Compuesto 1", ids);
        stack.Execute(create);
        string compound = create.CreatedIds[0];

        stack.Execute(Edits.AddEffect(SequenceId, compound, BuiltInEffects.Transform, ids: ids));
        stack.Execute(Edits.AddEffect(SequenceId, compound, BuiltInEffects.Blend, ids: ids));
        stack.Execute(Edits.AddTransition(SequenceId, compound, BuiltInTransitions.Crossfade, ids: ids));
        stack.Execute(Edits.AddMask(SequenceId, compound, MaskShape.Ellipse, ids: ids));

        Clip clip = Seq(project).FindClip(compound)!.Value.Clip;
        Assert.Equal(BuiltInEffects.Transform, clip.Effects[0].Type);
        Assert.Contains(clip.Effects, e => e.Type == BuiltInEffects.Blend);
        Assert.Contains(clip.Effects, e => e.Type == BuiltInEffects.Mask);
        Assert.Contains(Seq(project).VideoTracks[0].Transitions, t => t.From == compound && t.To == "clip-2");
    }

    // «Aplicar crea los clips … partidos a 42 caracteres por línea, dos líneas, de 1 a 7 s y cortando en pausas de 0,7 s.»
    [Fact]
    public void Subtitulos_automaticos_respetan_lineas_duracion_y_pausas()
    {
        var words = new List<SpeechWord>();
        double t = 0;
        for (int i = 0; i < 60; i++)
        {
            words.Add(new SpeechWord(S(t), S(t + 0.2), "palabra"));
            t += 0.25;
        }

        // Una pausa de 0,7 s parte aunque quepa más texto.
        words.Add(new SpeechWord(S(t + 0.7), S(t + 0.9), "fin"));
        var segments = new[] { new SpeechSegment(S(0), S(t + 0.9), string.Join(' ', words.Select(w => w.Text)), words) };
        IReadOnlyList<SubtitleCue> cues = SubtitleEdits.Layout(segments, SubtitleLayout.Default, Rational.Fps30);

        Assert.All(cues, c =>
        {
            string[] lines = c.Text.Split('\n');
            Assert.InRange(lines.Length, 1, 2);
            Assert.All(lines, l => Assert.InRange(l.Length, 1, 42));
            Assert.True(c.End - c.Start >= S(1) && c.End - c.Start <= S(7), $"«{c.Text}» dura {(c.End - c.Start).ToSeconds()} s.");
        });
        Assert.Equal("fin", cues[^1].Text);
    }

    // «Ctrl+Z lo deshace todo» y la pista nueva se llama «Subtítulos» con el rol de subtítulos.
    [Fact]
    public void Subtitulos_automaticos_son_un_solo_paso_con_pista_nueva()
    {
        (Project project, UndoStack stack) = New();
        string before = ProjectDump.Of(project);
        var segments = new[]
        {
            new SpeechSegment(S(20), S(22), "Hola a todos"),
            new SpeechSegment(S(24), S(26), "y adiós"),
        };
        var add = Edits.AddSubtitles(SequenceId, segments, new ClipGenerator { Style = new TextStyle() }, ids: Ids());
        stack.Execute(add);

        Track track = Seq(project).GetTrack(add.CreatedIds[^1]);
        Assert.Equal(("Subtítulos", TrackRoles.Subtitles), (track.Name, track.Role));
        Assert.Equal(["Hola a todos", "y adiós"], track.Clips.Select(c => c.Generator!.Text));
        Assert.Equal(1, stack.Count);
        stack.Undo();
        Assert.Equal(before, ProjectDump.Of(project));
    }

    // «Importar subtítulos… crea una pista de subtítulos nueva, con el nombre del archivo … si dos se pisan, el primero
    // termina donde empieza el segundo. Es un solo paso del historial.»
    [Fact]
    public void Importar_subtitulos_crea_la_pista_recorta_solapes_y_es_un_paso()
    {
        (Project project, UndoStack stack) = New();
        string before = ProjectDump.Of(project);
        SubtitleCue[] cues = [new(S(1), S(4), "Uno"), new(S(3), S(5), "Dos\nlíneas")];
        var import = Edits.ImportSubtitles(SequenceId, cues, new ClipGenerator { Style = new TextStyle() }, "entrevista", Ids());
        stack.Execute(import);

        Track track = Seq(project).GetTrack(import.CreatedIds[^1]);
        Assert.Equal(("entrevista", TrackRoles.Subtitles), (track.Name, track.Role));
        Assert.Equal([(S(1), S(3)), (S(3), S(5))], track.Clips.Select(c => (c.Start, c.End)));
        Assert.Equal("Dos\nlíneas", track.Clips[1].Generator!.Text);
        Assert.Equal(1, stack.Count);
        stack.Undo();
        Assert.Equal(before, ProjectDump.Of(project));
    }

    // «La tijera Dividir aquí parte el subtítulo en esa palabra; Unir con el siguiente los junta.»
    [Fact]
    public void Dividir_en_una_palabra_y_unir_devuelven_el_subtitulo()
    {
        (Project project, UndoStack stack) = New();
        var ids = Ids();
        var segments = new[]
        {
            new SpeechSegment(S(20), S(22), "uno dos tres",
                [new SpeechWord(S(20), S(20.5), "uno"), new SpeechWord(S(20.6), S(21), "dos"), new SpeechWord(S(21.2), S(22), "tres")]),
        };
        var add = Edits.AddSubtitles(SequenceId, segments, new ClipGenerator { Style = new TextStyle() }, ids: ids);
        stack.Execute(add);
        string clipId = add.CreatedIds[0];
        Clip clip = Seq(project).FindClip(clipId)!.Value.Clip;
        clip.Generator!.Words =
        [
            new GeneratorWord { Start = S(0), End = S(0.5), Text = "uno" },
            new GeneratorWord { Start = S(0.6), End = S(1), Text = "dos" },
            new GeneratorWord { Start = S(1.2), End = S(2), Text = "tres" },
        ];
        stack.MarkClean();
        string whole = ProjectDump.Of(project);

        var split = Edits.SplitSubtitleAtWord(SequenceId, clipId, 2, ids);
        stack.Execute(split);
        Track track = Seq(project).GetTrack(add.CreatedIds[^1]);
        Assert.Equal(["uno dos", "tres"], track.Clips.Select(c => c.Generator!.Text));
        Assert.Equal(S(21.2), track.Clips[1].Start);

        stack.Execute(Edits.JoinSubtitleWithNext(SequenceId, clipId, ids));
        Assert.Equal("uno dos tres", Assert.Single(track.Clips).Generator!.Text);
        Assert.Equal((S(20), S(22)), (track.Clips[0].Start, track.Clips[0].End));

        stack.Undo();
        stack.Undo();
        Assert.Equal(whole, ProjectDump.Of(project));
    }
}
