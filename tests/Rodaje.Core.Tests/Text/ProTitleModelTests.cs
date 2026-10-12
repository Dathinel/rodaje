using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Text;
using static Rodaje.Core.Tests.Model.ModelSamples;

namespace Rodaje.Core.Tests.Text;

/// <summary>Lo nuevo del estilo y del título (tipografía, degradado, varios bordes y sombras, arco, tramos, animación por unidades) en el modelo.</summary>
public class ProTitleModelTests
{
    private static Project WithTitle(out Clip title)
    {
        Project p = ValidProject();
        title = MakeTitle("t1", 10, -1, 1);
        p.Sequences[0].VideoTracks[1].Clips.Add(title);
        return p;
    }

    internal static TextStyle Rich()
    {
        TextStyle style = new()
        {
            Fill = new StyleFill
            {
                Color = "#FFFFFF",
                Gradient = new StyleGradient { Kind = GradientKind.Linear, Angle = 90, Stops = [new(0, "#FFD400"), new(1, "#FF3D00")] },
            },
            Stroke = new StyleStroke { Color = "#000000", Width = 4 },
            ExtraStrokes = [new StyleStroke { Color = "#FFFFFF", Width = 10 }],
            Shadow = new StyleShadow { Color = "#00000099", Dy = 6, Blur = 10 },
            ExtraShadows = [new StyleShadow { Color = "#FF000080", Dx = -8, Blur = 0 }],
            Typography = new StyleTypography { Kerning = false, Case = TextCase.SmallCaps, Features = ["liga", "-calt", "ss01"], Fit = TextFit.Shrink },
            Path = new StylePath { Kind = TextPathKind.Arc, Bend = 120 },
            Highlight = new StyleHighlight { Color = "#FFD400", Mode = HighlightMode.Sweep },
            In = TextAnimationPresets.Find("rebote")!.In(),
            Out = TextAnimationPresets.Find("desenfoque")!.Out(order: UnitOrder.Random),
        };
        return style;
    }

    [Fact]
    public void A_rich_title_validates_round_trips_and_clones()
    {
        Project p = WithTitle(out Clip title);
        title.Generator!.Text = "Hola mundo";
        title.Generator.Style = Rich();
        title.Generator.Box = new TextFrame { WidthFraction = 0.8, HeightFraction = 0.3 };
        title.Generator.Spans = [new TextSpan { Start = 0, Length = 4, Weight = 900, Color = "#00FF00" }, new TextSpan { Start = 5, Length = 5, Scale = 1.5, Italic = true, Underline = true }];
        Assert.Empty(ModelValidator.Validate(p));

        string json = ProjectSerializer.Serialize(p);
        Assert.Contains("\"mode\": \"sweep\"", json, StringComparison.Ordinal);
        Assert.Contains("\"case\": \"smallCaps\"", json, StringComparison.Ordinal);
        Assert.Contains("\"unit\": \"letter\"", json, StringComparison.Ordinal);
        Project back = ProjectSerializer.Deserialize(json);
        Assert.Equal(json, ProjectSerializer.Serialize(back));
        ClipGenerator read = back.Sequences[0].VideoTracks[1].Clips.Single(c => c.Id == "t1").Generator!;
        Assert.True(read.ContentEquals(title.Generator));
        Assert.True(read.Clone().ContentEquals(title.Generator));
        Assert.True(read.Style!.Clone().ContentEquals(title.Generator.Style));

        read.Style.ExtraStrokes![0].Width = 11;
        Assert.False(read.ContentEquals(title.Generator));
    }

    [Fact]
    public void A_plain_title_writes_none_of_the_new_fields()
    {
        Project p = WithTitle(out _);
        string json = ProjectSerializer.Serialize(p);
        foreach (string field in (string[])["extraStrokes", "extraShadows", "typography", "bend", "gradient", "spans", "unit", "stagger", "heightFraction"])
        {
            Assert.DoesNotContain($"\"{field}\"", json, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("degradado")]
    [InlineData("bordes")]
    [InlineData("funciones")]
    [InlineData("trayectoria")]
    [InlineData("desfase")]
    [InlineData("blur")]
    [InlineData("tramos")]
    [InlineData("alto")]
    public void Out_of_range_values_are_rejected(string what)
    {
        Project p = WithTitle(out Clip title);
        TextStyle style = Rich();
        title.Generator!.Style = style;
        switch (what)
        {
            case "degradado":
                style.Fill.Gradient!.Stops = [new(0.5, "#FFFFFF"), new(0.2, "#000000")];
                break;
            case "bordes":
                style.ExtraStrokes = [.. Enumerable.Range(0, 5).Select(_ => new StyleStroke { Width = 1 })];
                break;
            case "funciones":
                style.Typography!.Features = ["ligadura"];
                break;
            case "trayectoria":
                style.Path!.Bend = 400;
                break;
            case "desfase":
                style.In!.Stagger = 1.5;
                break;
            case "blur":
                style.In!.Curves[StyleAnimation.Blur] = [new(0, 500)];
                break;
            case "tramos":
                title.Generator.Spans = [new TextSpan { Start = 2, Length = 50, Weight = 700 }];
                break;
            case "alto":
                title.Generator.Box = new TextFrame { HeightFraction = 2 };
                break;
        }

        Assert.NotEmpty(ModelValidator.Validate(p));
    }

    [Fact]
    public void Null_items_in_the_new_lists_are_a_format_error()
    {
        Project p = WithTitle(out Clip title);
        title.Generator!.Style = Rich();
        string json = ProjectSerializer.Serialize(p).Replace("\"extraStrokes\": [", "\"extraStrokes\": [null,", StringComparison.Ordinal);
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(json));
    }
}
