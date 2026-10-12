using System.Numerics;
using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Player;
using Rodaje.Core.Model;
using Rodaje.Core.Text;
using Rodaje.Packages;
using Rodaje.Playback.Composition;

namespace Rodaje.App.Tests.Inspector;

/// <summary>Formato por tramos desde el panel, guardar estilos con animación (v2) y el ajuste de los títulos a las guías y zonas seguras.</summary>
public class ProTitleEditorTests
{
    private static async Task<(EditorSession Session, TitleEditor Editor)> WithTitle(TextStyle? style = null)
    {
        var prober = new FakeProber().Video("a.mp4", 10);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        Assert.True(session.AddTitleAtPlayhead(style));
        return (session, new TitleEditor(session));
    }

    [Fact]
    public void Negrita_SobreLoElegido_EsUnPasoYSeQuitaConOtroClic() => UiThread.Run(async () =>
    {
        (EditorSession session, TitleEditor editor) = await WithTitle();
        editor.SetText("Hola mundo");
        editor.EndTyping();
        int steps = session.History.Count;

        Assert.True(editor.Toggle(5, 5, s => s.Weight >= 600, (s, on) => s with { Weight = on ? 700 : null }, "Negrita"));
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal("Negrita", session.UndoDescription);
        Assert.Equal(700, editor.SpanAt(7)!.Weight);
        Assert.Null(editor.SpanAt(2));

        Assert.True(editor.FormatText(5, 5, s => s with { Color = "#FF0000" }, "Color del texto"));
        Assert.True(editor.Toggle(5, 5, s => s.Weight >= 600, (s, on) => s with { Weight = on ? 700 : null }, "Negrita"));
        Assert.Equal(new TextSpan { Start = 5, Length = 5, Color = "#FF0000" }, Assert.Single(session.SelectedTitle!.Generator!.Spans!));

        // Escribir delante mueve el tramo con su palabra.
        editor.SetText("¡Hola mundo");
        Assert.Equal(6, editor.SpanAt(6)!.Start);
        Assert.False(editor.FormatText(3, 0, s => s with { Italic = true }));
    });

    [Fact]
    public void GuardarEstilo_ConSuAnimacionPorLetra_LoLeeLaBibliotecaIgual() => UiThread.Run(async () =>
    {
        using var folder = new TempDirectory();
        TextStyle style = TitleStyles.Default();
        style.Fill.Gradient = new StyleGradient { Angle = 45, Stops = [new ColorStop(0, "#FFD400"), new ColorStop(1, "#FF3D00")] };
        style.ExtraStrokes = [new StyleStroke { Color = "#FFFFFF", Width = 10 }];
        style.ExtraShadows = [new StyleShadow { Color = "#FF00FF80", Blur = 20 }];
        style.Typography = new StyleTypography { Kerning = false, Case = TextCase.Upper, Features = ["tnum"], Fit = TextFit.Shrink };
        style.Path = new StylePath { Bend = -60 };
        style.Highlight = new StyleHighlight { Mode = HighlightMode.Sweep };
        style.In = TextAnimationPresets.Find("rebote")!.In(TextUnit.Letter, 0.5);
        style.Out = TextAnimationPresets.Find("desenfoque")!.Out(TextUnit.Word, 0.3, order: UnitOrder.Random);
        (EditorSession session, TitleEditor editor) = await WithTitle(style);
        string own = Path.Combine(folder.Path, "propios");

        (string path, string reference) = editor.SaveStyle(own, "Neón animado")!.Value;
        Assert.Contains("\"version\": 2", File.ReadAllText(path), StringComparison.Ordinal);
        PackageScan scan = PackageLibrary.Scan(new PackageLocations(Path.Combine(folder.Path, "p"), Path.Combine(folder.Path, "m"), own, []), "rodaje");
        var loader = new InstalledContentLoader(() => scan);
        TextStyle read = TitleStyles.FromPreset(loader.LoadTextStyle(reference)!, reference);
        Assert.True(read.ContentEquals(session.SelectedTitle!.Generator!.Style!));

        (string still, string stillReference) = editor.SaveStyle(own, "Neón quieto", withAnimation: false)!.Value;
        PackageScan again = PackageLibrary.Scan(new PackageLocations(Path.Combine(folder.Path, "p"), Path.Combine(folder.Path, "m"), own, []), "rodaje");
        TextStyle quiet = TitleStyles.FromPreset(new InstalledContentLoader(() => again).LoadTextStyle(stillReference)!, stillReference);
        Assert.Null(quiet.In);
        Assert.Null(quiet.Out);
        Assert.True(File.Exists(still));
    });

    [Fact]
    public void UnEstiloSinNadaNuevo_SeGuardaEnLaVersion1()
    {
        Assert.False(TitleStyleFile.NeedsVersion2(TitleStyles.Default()));
        TextStyle block = TitleStyles.Default();
        block.In = new StyleAnimation { DurationSeconds = 1, Curves = new() { ["opacity"] = [new StyleKey(0, 0), new StyleKey(1, 1)] } };
        Assert.False(TitleStyleFile.NeedsVersion2(block));
        block.In.Unit = TextUnit.Word;
        Assert.True(TitleStyleFile.NeedsVersion2(block));
    }

    [Fact]
    public void UnTitulo_SePegaAlMargenSeguroYALasZonasQueSeVen()
    {
        IReadOnlyList<SnapLine> lines = SnapGuides.SafeLines(1080, 1920, [(0.0, 0.8, 1.0, 0.2)]);
        Assert.Contains(lines, l => l.Vertical && Math.Abs(l.At - 54) < 0.01);
        Assert.Contains(lines, l => !l.Vertical && Math.Abs(l.At - 1536) < 0.01);
        Assert.DoesNotContain(lines, l => !l.Vertical && l.At >= 1919.5);

        // Un título de 400 × 100 cuyo borde de abajo queda a 5 px de la zona de abajo se pega a ella.
        (Vector2 correction, IReadOnlyList<SnapLine> hits) = SnapGuides.Snap(new Vector2(700, 1481), new Vector2(200, 50), 1080, 1920, 1, lines);
        Assert.Equal(5, correction.Y, 3);
        Assert.Contains(hits, h => h.Kind == SnapKind.Safe && !h.Vertical);

        Vector2 half = TitleHandles.HalfSize(new TitleBox("t", LayerTransform.Identity with { Scale = 2, RotationDegrees = 90 }, 100, 50));
        Assert.Equal(50, half.X, 3);
        Assert.Equal(100, half.Y, 3);
    }
}
