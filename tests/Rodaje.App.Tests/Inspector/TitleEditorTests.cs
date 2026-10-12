using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Paquetes;

namespace Rodaje.App.Tests.Inspector;

/// <summary>La pestaña «Texto»: el tecleo agrupado en un paso, los cambios de estilo y las animaciones.</summary>
public class TitleEditorTests
{
    /// <summary>Un reloj que solo avanza cuando la prueba lo pide.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private static async Task<(EditorSession Session, TitleEditor Editor, ManualClock Clock)> WithTitle(TextStyle? style = null)
    {
        var prober = new FakeProber().Video("a.mp4", 10);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        Assert.True(session.AddTitleAtPlayhead(style));
        var clock = new ManualClock();
        return (session, new TitleEditor(session, clock), clock);
    }

    private static Clip Title(EditorSession session) => session.SelectedTitle!;

    [Fact]
    public void Escribir_UnaTandaEsUnPaso_YUnaPausaEmpiezaOtro() => UiThread.Run(async () =>
    {
        (EditorSession session, TitleEditor editor, ManualClock clock) = await WithTitle();
        int steps = session.History.Count;

        foreach (string text in new[] { "H", "Ho", "Hol", "Hola" })
        {
            clock.Advance(TimeSpan.FromMilliseconds(150));
            Assert.True(editor.SetText(text));
        }

        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal("Escribir texto", session.UndoDescription);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(editor.SetText("Hola mundo"));
        Assert.Equal(steps + 2, session.History.Count);

        // Salir del campo también cierra la tanda.
        editor.EndTyping();
        Assert.True(editor.SetText("Hola mundo!"));
        Assert.Equal(steps + 3, session.History.Count);
        Assert.False(editor.SetText("Hola mundo!"));

        Assert.True(session.Undo());
        Assert.True(session.Undo());
        Assert.Equal("Hola", Title(session).Generator!.Text);
        Assert.True(session.Undo());
        Assert.Equal(TitleStyles.DefaultText, Title(session).Generator!.Text);
    });

    [Fact]
    public void UnDeslizadorEsUnPasoHastaSoltar_YUnaCasillaUnPasoPropio() => UiThread.Run(async () =>
    {
        (EditorSession session, TitleEditor editor, _) = await WithTitle();
        int steps = session.History.Count;

        for (int size = 100; size <= 140; size += 10)
        {
            int value = size;
            Assert.True(editor.ChangeStyle("font-size", s => s.Size = value));
        }

        editor.EndChange();
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal(140, Title(session).Generator!.Style!.Size);

        Assert.True(editor.ChangeStyle("font-size", s => s.Size = 150));
        Assert.Equal(steps + 2, session.History.Count);

        Assert.True(editor.CommitStyle("stroke-on", s => s.Stroke = new StyleStroke { Color = "#000000", Width = 4 }));
        Assert.True(editor.CommitStyle("stroke-on", s => s.Stroke = null));
        Assert.Equal(steps + 4, session.History.Count);

        // Sin cambio real no hay paso.
        Assert.False(editor.CommitStyle("font-size", s => s.Size = 150));
        Assert.Equal(steps + 4, session.History.Count);

        // Un estilo inválido avisa y no cambia nada.
        Assert.False(editor.CommitStyle("font-size", s => s.Size = 5000));
        Assert.Equal(150, Title(session).Generator!.Style!.Size);
    });

    /// <summary>
    /// «Guardar como estilo» escribe un <c>rodaje.textstyle</c> en los propios que la biblioteca lee tal cual, y el título
    /// pasa a salir de ahí; «Reaplicar estilo» vuelve a copiarlo sobre un título cambiado.
    /// </summary>
    [Fact]
    public void GuardarComoEstiloYReaplicarloDesdeSuOrigen() => UiThread.Run(async () =>
    {
        using var folder = new TempDirectory();
        TextStyle full = TitleStyles.Default();
        full.Stroke = new StyleStroke { Color = "#102030", Width = 6, Join = StrokeJoin.Bevel };
        full.Background = new StyleBackground { Color = "#00000080", Padding = [24, 12], Radius = 12 };
        full.Align = TextAlign.Left;
        full.Font.Italic = true;
        full.In = new StyleAnimation
        {
            DurationSeconds = 0.4,
            Curves = { ["opacity"] = [new StyleKey(0, 0, Interpolation.EaseOut), new StyleKey(0.4, 1)], ["y"] = [new StyleKey(0, 40), new StyleKey(0.4, 0)] },
        };
        full.Out = new StyleAnimation { DurationSeconds = 0.3, Curves = { ["scale"] = [new StyleKey(0, 1), new StyleKey(0.3, 1.5, Interpolation.EaseIn)] } };
        (EditorSession session, TitleEditor editor, _) = await WithTitle(full);
        string own = Path.Combine(folder.Path, "propios");

        (string path, string reference) = editor.SaveStyle(own, "Mi Neón")!.Value;
        Assert.True(File.Exists(path));
        Assert.Equal("propios@0.0.0/mi-neon", reference);
        Assert.Equal(reference, Title(session).Generator!.Style!.Source);
        Assert.Equal("Mi Neón", Title(session).Generator!.Style!.Name);

        PackageScan scan = PackageLibrary.Scan(new PackageLocations(Path.Combine(folder.Path, "p"), Path.Combine(folder.Path, "m"), own, []), "rodaje");
        var loader = new InstalledContentLoader(() => scan);
        TextStyle read = TitleStyles.FromPreset(loader.LoadTextStyle(reference)!, reference);
        Assert.True(read.ContentEquals(Title(session).Generator!.Style!));

        int steps = session.History.Count;
        Assert.True(editor.CommitStyle("font-size", s => s.Size = 30));
        Assert.Equal(ReapplyResult.Applied, editor.ReapplyStyle(loader));
        Assert.Equal(96, Title(session).Generator!.Style!.Size);
        Assert.Equal(steps + 2, session.History.Count);
        Assert.Equal(ReapplyResult.Unchanged, editor.ReapplyStyle(loader));

        Assert.True(editor.CommitStyle("source", s => s.Source = "rodaje-titulos@1.0.0/no-esta"));
        Assert.Equal(ReapplyResult.Missing, editor.ReapplyStyle(loader));
        Assert.True(editor.CommitStyle("source", s => s.Source = null));
        Assert.Equal(ReapplyResult.NoSource, editor.ReapplyStyle(loader));
    });

    [Fact]
    public void LosSubtitulosNacenLegiblesYElAvisoMiraTamanoYContraste()
    {
        TextStyle subtitle = SubtitleStyles.Default();
        Assert.Null(SubtitleStyles.Legibility(subtitle, 1920, 1080));
        Assert.Null(SubtitleStyles.Legibility(subtitle, 1080, 1920));
        Assert.Equal(0.8, SubtitleStyles.NewSubtitle().Box!.WidthFraction);

        TextStyle small = subtitle.Clone();
        small.Size = 30;
        Assert.Contains("px", SubtitleStyles.Legibility(small, 1920, 1080), StringComparison.Ordinal);

        TextStyle bare = TitleStyles.Default();
        Assert.Contains("Sin fondo ni borde", SubtitleStyles.Legibility(bare, 1920, 1080), StringComparison.Ordinal);
        bare.Stroke = new StyleStroke { Color = "#000000", Width = 6 };
        Assert.Null(SubtitleStyles.Legibility(bare, 1920, 1080));

        TextStyle grey = subtitle.Clone();
        grey.Fill.Color = "#555555";
        Assert.Contains(":1", SubtitleStyles.Legibility(grey, 1920, 1080), StringComparison.Ordinal);
        Assert.Equal(21, SubtitleStyles.Contrast((255, 255, 255, 255), (255, 0, 0, 0)), 3);
    }

    [Fact]
    public void UnTextoSueltoEnUnaPistaDeSubtitulosTomaElEstiloDeSubtitulos() => UiThread.Run(async () =>
    {
        (EditorSession session, _, _) = await WithTitle();
        Assert.True(session.AddSubtitleTrack());
        string track = session.Sequence.VideoTracks[^1].Id;
        Assert.True(session.AddTitleAt(track, Rodaje.Core.Timing.Time.FromSeconds(6), TitleStyles.Default()));
        Assert.Equal("Subtítulo", Title(session).Generator!.Style!.Name);

        TextStyle mine = TitleStyles.Default();
        mine.Size = 70;
        Assert.True(session.AddTitleAt(track, Rodaje.Core.Timing.Time.FromSeconds(12), mine));
        Assert.Equal(70, Title(session).Generator!.Style!.Size);
    });

    [Fact]
    public void AplicarOtroEstilo_SustituyeLaCopia() => UiThread.Run(async () =>
    {
        (EditorSession session, TitleEditor editor, _) = await WithTitle();
        TextStyle other = TitleStyles.Default();
        other.Source = "rodaje-titulos@1.0.0/neon";
        other.Font.Family = "Impact";
        Assert.True(editor.ApplyStyle(other));
        Assert.Equal("Impact", Title(session).Generator!.Style!.Font.Family);
        Assert.Equal("rodaje-titulos@1.0.0/neon", Title(session).Generator!.Style!.Source);
    });

    [Fact]
    public void BordesYConvertirEnKeyframes() => UiThread.Run(async () =>
    {
        TextStyle animated = TitleStyles.Default();
        animated.In = new StyleAnimation { DurationSeconds = 0.5, Curves = { ["opacity"] = [new StyleKey(0, 0), new StyleKey(0.5, 1)] } };
        animated.Out = new StyleAnimation { DurationSeconds = 0.5, Curves = { ["scale"] = [new StyleKey(0, 1), new StyleKey(0.5, 0.5)] } };
        (EditorSession session, TitleEditor editor, _) = await WithTitle(animated);

        Assert.Equal("Entrada de 0,5 s · opacidad", TitleEditor.DescribeAnimation(animated.In, entry: true));
        Assert.Equal("Salida de 0,5 s · tamaño", TitleEditor.DescribeAnimation(animated.Out, entry: false));
        Assert.Null(TitleEditor.DescribeAnimation(null, entry: true));

        Assert.True(editor.SetEdges(true, false));
        Assert.False(Title(session).Generator!.Edges.Out);

        Assert.True(editor.ConvertToKeyframes());
        Clip clip = Title(session);
        Assert.Null(clip.Generator!.Style!.In);
        Assert.NotNull(clip.Generator.Style.Out);
        EffectInstance transform = Assert.Single(clip.Effects);
        Assert.Equal(2, transform.Parameters["opacity"].Keyframes.Count);
        Assert.Equal("Convertir en keyframes", session.UndoDescription);
    });

    [Fact]
    public void SinTituloSeleccionadoNoHaceNada() => UiThread.Run(async () =>
    {
        (EditorSession session, TitleEditor editor, _) = await WithTitle();
        session.ClearSelection();
        Assert.Null(editor.Target());
        Assert.False(editor.SetText("x"));
        Assert.False(editor.ChangeStyle("font-size", s => s.Size = 10));
        Assert.False(editor.SetEdges(false, false));
        Assert.False(editor.ConvertToKeyframes());
        Assert.False(editor.ApplyStyle(TitleStyles.Default()));
    });

    [Theory]
    [InlineData("#ffffff", "#FFFFFF")]
    [InlineData("00000080", "#00000080")]
    [InlineData(" #12345678 ", "#12345678")]
    [InlineData("#123456FF", "#123456")]
    [InlineData("#FFF", null)]
    [InlineData("blanco", null)]
    [InlineData("#-1234567", null)]
    public void LosColoresSeNormalizan(string text, string? expected) => Assert.Equal(expected, StyleColors.Normalize(text));

    [Fact]
    public void LosPesosTienenNombre()
    {
        Assert.Equal("Negrita", TitleEditor.WeightName(700));
        Assert.Equal("350", TitleEditor.WeightName(350));
        Assert.Contains("Segoe UI", TitleEditor.CommonFonts);
    }
}
