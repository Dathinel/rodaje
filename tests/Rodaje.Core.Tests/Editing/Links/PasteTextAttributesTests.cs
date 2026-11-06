using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Links;

/// <summary>«Pegar atributos» entre textos y subtítulos: el estilo y las sombras viajan, las palabras no.</summary>
public sealed class PasteTextAttributesTests
{
    private static TrimFixture Titles()
    {
        var f = new TrimFixture().AddTitle("v1", "src", 0, 50, 0).AddTitle("v1", "dst", 0, 50, 60).Add("v2", "video", "m1", 0, 50, 0);
        TextStyle style = f.Clip("src").Generator!.Style!;
        style.Font = new StyleFont { Family = "Impact", Weight = 900 };
        style.Size = 120;
        style.LineHeight = 1.5;
        style.Align = TextAlign.Left;
        style.Fill = new StyleFill { Color = "#FFCC00" };
        style.Stroke = new StyleStroke { Color = "#112233", Width = 8 };
        style.Shadow = new StyleShadow { Color = "#000000AA", Dx = 5, Dy = 7, Blur = 9 };
        style.ExtraShadows = [new StyleShadow { Color = "#FF0000", Dx = 1, Dy = 2, Blur = 3 }];
        f.Clip("src").Generator!.Text = "Origen";
        f.Clip("dst").Generator!.Text = "Destino";
        f.Clip("dst").Generator!.Style = new TextStyle { Size = 40 };
        return f;
    }

    [Fact]
    public void Text_style_with_shadows_and_stroke_is_copied_but_not_the_words()
    {
        TrimFixture f = Titles();
        ClipAttributes copied = ClipAttributes.Capture(f.Sequence, "src");
        Assert.True((copied.Available & ClipAttributeKinds.Text) != 0 && (copied.Available & ClipAttributeKinds.Shadow) != 0);

        ClipAttributeEdits.Paste(f.Ctx, ["dst"], copied, ClipAttributeKinds.Text | ClipAttributeKinds.Shadow);

        f.AssertValid();
        ClipGenerator target = f.Clip("dst").Generator!;
        Assert.Equal("Destino", target.Text);
        TextStyle style = target.Style!;
        Assert.True(style.ContentEquals(f.Clip("src").Generator!.Style!));
        Assert.NotSame(f.Clip("src").Generator!.Style, style);
        Assert.Equal(("Impact", 120d, 1.5, TextAlign.Left), (style.Font.Family, style.Size, style.LineHeight, style.Align));
        Assert.Equal(8, style.Stroke!.Width);
        Assert.Equal(9, style.Shadow!.Blur);
        Assert.Single(style.ExtraShadows!);
    }

    [Fact]
    public void Shadows_alone_leave_the_rest_of_the_style()
    {
        TrimFixture f = Titles();
        ClipAttributeEdits.Paste(f.Ctx, ["dst"], ClipAttributes.Capture(f.Sequence, "src"), ClipAttributeKinds.Shadow);

        TextStyle style = f.Clip("dst").Generator!.Style!;
        Assert.Equal(40, style.Size);
        Assert.Null(style.Stroke);
        Assert.Equal(7, style.Shadow!.Dy);
        Assert.Single(style.ExtraShadows!);
    }

    [Fact]
    public void From_text_to_video_only_the_common_attributes_apply_and_video_to_text_has_no_text_style()
    {
        TrimFixture f = Titles();
        f.Clip("src").Effects.Add(new EffectInstance { Id = "fx", Type = BuiltInEffects.Blur });
        ClipAttributeEdits.Paste(f.Ctx, ["video"], ClipAttributes.Capture(f.Sequence, "src"), ClipAttributeKinds.All);
        Assert.Equal([BuiltInEffects.Blur], f.Clip("video").Effects.Select(e => e.Type));
        Assert.Null(f.Clip("video").Generator);

        ClipAttributes fromVideo = ClipAttributes.Capture(f.Sequence, "video");
        Assert.Equal(ClipAttributeKinds.None, fromVideo.Available & (ClipAttributeKinds.Text | ClipAttributeKinds.Shadow));
        Assert.Throws<EditException>(() => ClipAttributeEdits.Paste(f.Ctx, ["dst"], fromVideo, ClipAttributeKinds.Text));
    }

    [Fact]
    public void Subtitle_style_is_pasted_between_subtitles_keeping_each_words()
    {
        TrimFixture f = Titles();
        f.Track("v1").Role = TrackRoles.Subtitles;
        f.Clip("src").Generator!.Words = [new GeneratorWord { Text = "Origen", Start = F(0), End = F(10) }];
        f.Clip("src").Generator!.Style!.Highlight = new StyleHighlight { Color = "#00FF00" };
        ClipAttributeEdits.Paste(f.Ctx, ["dst"], ClipAttributes.Capture(f.Sequence, "src"), ClipAttributeKinds.Text);

        ClipGenerator target = f.Clip("dst").Generator!;
        Assert.Equal("#00FF00", target.Style!.Highlight!.Color);
        Assert.Null(target.Words);
        Assert.Equal("Destino", target.Text);
    }
}
