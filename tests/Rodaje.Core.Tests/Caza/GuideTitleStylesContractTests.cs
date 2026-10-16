using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Tests.Model;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Lo que promete «Estilos y fuentes de paquetes» de <c>docs/guia/titulos-y-subtitulos.md</c>, comprobado en el modelo y
/// las ediciones de Core. Cada prueba cita la frase.
/// </summary>
public sealed class GuideTitleStylesContractTests
{
    private const string PackageStyle = "rodaje-titulos@1.0.0/clasico";
    private const string PackageFont = "rodaje-fuentes@1.0.0/inter";

    // «Un estilo de paquete se copia al título con su origen»
    [Fact]
    public void Package_style_is_copied_to_the_title_with_its_origin()
    {
        TrimFixture f = new TrimFixture().AddTitle("v2", "t1", 0, 50, 0);
        f.Clip("t1").Generator!.Style = null;
        TextStyle style = ModelSamples.FullStyle();

        TitleEdits.SetStyle(f.Ctx, "t1", style);

        TextStyle copy = f.Clip("t1").Generator!.Style!;
        Assert.NotSame(style, copy);
        Assert.True(copy.ContentEquals(style));
        Assert.Equal(PackageStyle, copy.Source);
        Assert.Equal(PackageFont, copy.Font.Source);

        // Es una copia: lo que cambie después en el estilo de origen no llega al título.
        style.Size = 12;
        style.Font.Family = "Otra";
        style.Fill.Color = "#123456";
        Assert.Equal(96, copy.Size);
        Assert.Equal("Inter", copy.Font.Family);
        Assert.Equal("#FFFFFF", copy.Fill.Color);
        f.AssertValid();
    }

    // «Un estilo de paquete se copia al título con su origen»
    [Fact]
    public void New_title_with_a_package_style_keeps_its_origin()
    {
        TrimFixture f = new();
        TextStyle style = ModelSamples.FullStyle();
        string id = TitleEdits.InsertTitle(f.Ctx, F(10), TitleEdits.DefaultDuration, new ClipGenerator { Text = "Hola", Style = style }, "Texto")[0];

        TextStyle copy = f.Clip(id).Generator!.Style!;
        Assert.Equal(PackageStyle, copy.Source);
        Assert.Equal(PackageFont, copy.Font.Source);
        Assert.True(copy.ContentEquals(ModelSamples.FullStyle()));
        f.AssertValid();
    }

    // «Un estilo de paquete se copia al título con su origen»: el comando del historial también copia.
    [Fact]
    public void Style_command_copies_the_style_when_created()
    {
        TextStyle style = ModelSamples.FullStyle();
        Rodaje.Core.Commands.SequenceEditCommand command = Rodaje.Core.Commands.Edits.SetTitleStyle("seq", "t1", style);
        TrimFixture f = new TrimFixture().AddTitle("v2", "t1", 0, 50, 0);
        style.Size = 12;
        command.Apply(f.Project);

        TextStyle copy = f.Clip("t1").Generator!.Style!;
        Assert.Equal(96, copy.Size);
        Assert.Equal(PackageStyle, copy.Source);
    }

    // «así que el proyecto sigue abriendo aunque desinstales el paquete»
    [Fact]
    public void Project_with_a_package_style_opens_without_the_package()
    {
        Project project = ModelSamples.ValidProject();
        Clip title = ModelSamples.MakeTitle("t1", 10, 0, 5, "Hola");
        title.Generator!.Style!.Typography = new StyleTypography { Case = TextCase.Upper, Features = ["ss01"] };
        title.Generator.Style.Highlight = new StyleHighlight { Color = "#FFD400", Mode = HighlightMode.Sweep };
        project.Sequences[0].VideoTracks[1].Clips.Add(title);
        project.Sequences[0].VideoTracks[1].SortClips();

        // Core no consulta los paquetes instalados: abrir solo lee la copia del proyecto.
        Project loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

        TextStyle copy = loaded.Sequences[0].GetClip("t1").Clip.Generator!.Style!;
        Assert.True(copy.ContentEquals(title.Generator.Style));
        Assert.Equal(PackageStyle, copy.Source);
        Assert.Equal(PackageFont, copy.Font.Source);
        Assert.Equal(["Segoe UI", "Arial"], copy.Font.Fallback);
    }

    // «Los paquetes pueden traer estilos de texto … y fuentes»: la fuente de paquete queda como referencia del estilo.
    [Fact]
    public void Font_origin_must_be_a_package_reference()
    {
        TrimFixture f = new TrimFixture().AddTitle("v2", "t1", 0, 50, 0);
        TextStyle style = ModelSamples.FullStyle();
        style.Font.Source = "no es una referencia";

        Assert.ThrowsAny<Exception>(() => Rodaje.Core.Commands.Edits.SetTitleStyle("seq", "t1", style).Apply(f.Project));
        Assert.Equal(PackageFont, f.Clip("t1").Generator!.Style!.Font.Source);
    }
}
