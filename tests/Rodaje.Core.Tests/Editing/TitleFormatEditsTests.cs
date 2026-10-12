using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Text;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing;

/// <summary>Formato por tramos al editar: los tramos siguen al texto y las animaciones por unidades no se pasan a keyframes.</summary>
public class TitleFormatEditsTests
{
    [Fact]
    public void Formatting_a_word_and_typing_keeps_it_on_that_word()
    {
        TrimFixture f = new TrimFixture().AddTitle("v2", "t", 0, 50, 0);
        TitleEdits.SetText(f.Ctx, "t", "Hola mundo");
        TitleEdits.FormatText(f.Ctx, "t", 5, 5, s => s with { Weight = 800, Color = "#FF0000" });
        ClipGenerator generator = f.Clip("t").Generator!;
        Assert.Equal([new TextSpan { Start = 5, Length = 5, Weight = 800, Color = "#FF0000" }], generator.Spans!);

        TitleEdits.SetText(f.Ctx, "t", "¡Hola mundo");
        TitleEdits.SetText(f.Ctx, "t", "¡Hola mundo!");
        Assert.Equal((6, 6), (generator.Spans![0].Start, generator.Spans[0].Length));
        TitleEdits.SetText(f.Ctx, "t", "¡Hola!");
        Assert.Equal((5, 1), (generator.Spans![0].Start, generator.Spans[0].Length));
        TitleEdits.SetText(f.Ctx, "t", "¡Hola");
        Assert.Null(generator.Spans);

        f.AssertValid();
    }

    [Fact]
    public void Formatting_needs_a_piece_of_the_text()
    {
        TrimFixture f = new TrimFixture().AddTitle("v2", "t", 0, 50, 0);
        Assert.Throws<EditException>(() => TitleEdits.FormatText(f.Ctx, "t", 0, 0, s => s with { Italic = true }));
        Assert.Throws<EditException>(() => TitleEdits.FormatText(f.Ctx, "t", 2, 500, s => s with { Italic = true }));
    }

    [Fact]
    public void Per_unit_animations_stay_in_the_style_when_converting_to_keyframes()
    {
        TrimFixture f = new TrimFixture().AddTitle("v2", "t", 0, 50, 0);
        TextStyle style = f.Clip("t").Generator!.Style!.Clone();
        style.In = TextAnimationPresets.Find("maquina")!.In();
        style.Out = new StyleAnimation { DurationSeconds = 0.5, Curves = new() { [BuiltInEffects.TransformParameters.Opacity] = [new(0, 1), new(0.5, 0)] } };
        TitleEdits.SetStyle(f.Ctx, "t", style);
        TitleEdits.ConvertAnimationToKeyframes(f.Ctx, "t");
        TextStyle after = f.Clip("t").Generator!.Style!;
        Assert.NotNull(after.In);
        Assert.Null(after.Out);

        Assert.Throws<EditException>(() => TitleEdits.ConvertAnimationToKeyframes(f.Ctx, "t"));
    }
}
