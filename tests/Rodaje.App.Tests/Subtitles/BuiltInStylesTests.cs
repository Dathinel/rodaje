using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Titles;
using Rodaje.App.Tests.Effects;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Rodaje.Packages;

namespace Rodaje.App.Tests.Subtitles;

/// <summary>Los cuatro estilos de subtítulo animados que trae la app en la categoría «Texto» de la biblioteca.</summary>
public sealed class BuiltInStylesTests
{
    private static readonly string[] References =
    [
        TitleStyles.BuiltInReference,
        BuiltInStyles.WordsReference,
        BuiltInStyles.KaraokeReference,
        BuiltInStyles.PopReference,
        BuiltInStyles.LinesReference,
    ];

    [Fact]
    public void LaCategoriaTextoTraeLosCincoDeFabrica_YFindLosResuelve()
    {
        IReadOnlyList<LibraryElement> text = LibraryElements.BuiltIn(LibraryCategory.Text);
        Assert.Equal(References, text.Select(e => e.Reference));
        Assert.Equal(["Texto", "Palabra a palabra", "Karaoke", "Pop", "Líneas"], text.Select(e => e.Name));
        Assert.All(text, e => Assert.True(e.IsBuiltIn));

        foreach (string reference in References)
        {
            TextStyle style = Assert.IsType<TextStyle>(BuiltInStyles.Find(reference));
            // Cada llamada es una copia: cambiarla no toca el registro.
            Assert.NotSame(style, BuiltInStyles.Find(reference));
        }

        Assert.Null(BuiltInStyles.Find("rodaje-titulos@1.0.0/neon"));
        Assert.Null(BuiltInStyles.Find(null));
        Assert.Null(BuiltInStyles.Find(""));
    }

    [Fact]
    public void LosCuatroDeSubtitulo_SalenDelEstiloDeSubtitulos_YSeLeanEnCualquierCuadro()
    {
        foreach (string reference in References.Skip(1))
        {
            TextStyle style = BuiltInStyles.Find(reference)!;
            Assert.Equal(68, style.Size);
            // De fábrica, sin origen de paquete (como «Texto»): el origen no puede llevar una referencia de motor.
            Assert.Null(style.Source);
            Assert.Null(SubtitleStyles.Legibility(style, 1920, 1080));
            Assert.Null(SubtitleStyles.Legibility(style, 1080, 1920));
        }
    }

    [Fact]
    public void CadaUnoLlevaSuAnimacionYSuResaltado()
    {
        TextStyle words = BuiltInStyles.Find(BuiltInStyles.WordsReference)!;
        Assert.Equal((TextUnit.Word, 0.5, 0.5), (words.In!.Unit, words.In.Stagger, words.In.DurationSeconds));
        Assert.Equal((HighlightMode.Word, "#FFD400"), (words.Highlight!.Mode, words.Highlight.Color));
        Assert.NotNull(words.Background);

        TextStyle karaoke = BuiltInStyles.Find(BuiltInStyles.KaraokeReference)!;
        Assert.Null(karaoke.In);
        Assert.Equal((HighlightMode.Sweep, "#FFD400"), (karaoke.Highlight!.Mode, karaoke.Highlight.Color));

        TextStyle pop = BuiltInStyles.Find(BuiltInStyles.PopReference)!;
        Assert.Equal((TextUnit.Word, 0.6), (pop.In!.Unit, pop.In.DurationSeconds));
        Assert.Null(pop.Background);
        Assert.Equal("#000000", pop.Stroke!.Color);
        Assert.InRange(pop.Stroke.Width, 4, pop.Size / 2);
        Assert.Equal(HighlightMode.Word, pop.Highlight!.Mode);

        TextStyle lines = BuiltInStyles.Find(BuiltInStyles.LinesReference)!;
        Assert.Equal(TextUnit.Line, lines.In!.Unit);
        Assert.Equal(TextUnit.Block, lines.Out!.Unit);
        Assert.Null(lines.Highlight);
    }

    [Fact]
    public void PasanElValidadorYSobrevivenALaIdaYVuelta()
    {
        var project = new Project
        {
            Id = "p",
            Name = "p",
            Sequences =
            [
                new Sequence
                {
                    Id = "s",
                    Name = "s",
                    VideoTracks = [new Track { Id = "v1", Kind = TrackKind.Video, Name = "V1" }],
                    AudioTracks = [new Track { Id = "a1", Kind = TrackKind.Audio, Name = "A1" }],
                },
            ],
        };

        Track track = project.Sequences[0].VideoTracks[0];
        List<TextStyle> styles = [.. References.Select(r => BuiltInStyles.Find(r)!)];
        for (int i = 0; i < styles.Count; i++)
        {
            track.Clips.Add(new Clip
            {
                Id = $"t{i}",
                Start = Time.FromSeconds(i * 6),
                SourceOut = Time.FromSeconds(5),
                Generator = new ClipGenerator { Kind = GeneratorKinds.Text, Text = "Hola\nmundo", Style = styles[i] },
            });
        }

        Assert.Empty(ModelValidator.Validate(project));

        Project loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        List<Clip> clips = [.. loaded.Sequences[0].VideoTracks[0].Clips];
        Assert.Equal(styles.Count, clips.Count);
        for (int i = 0; i < clips.Count; i++)
        {
            Assert.True(styles[i].ContentEquals(clips[i].Generator!.Style!), $"El estilo {References[i]} no volvió igual.");
        }
    }

    [Fact]
    public void ContentEqualsLosDistingue()
    {
        for (int i = 0; i < References.Length; i++)
        {
            TextStyle style = BuiltInStyles.Find(References[i])!;
            Assert.True(style.ContentEquals(BuiltInStyles.Find(References[i])!));
            for (int j = 0; j < References.Length; j++)
            {
                if (i != j)
                {
                    Assert.False(style.ContentEquals(BuiltInStyles.Find(References[j])!), $"{References[i]} frente a {References[j]}");
                }
            }
        }
    }

    /// <summary>Al aplicar un estilo de fábrica el título lleva su copia tal cual, y la lista lo reconoce por su contenido.</summary>
    [Fact]
    public void AplicarUnoDejaElEstiloDeFabrica_YLaListaLoEncuentra() => UiThread.Run(async () =>
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        var applier = new ElementApplier(session, new FakeContentLoader());
        ChoiceList<LibraryElement> styles = TextStyleChoices.Build(PackageScan.Empty);

        int i = 0;
        foreach (LibraryElement element in LibraryElements.BuiltIn(LibraryCategory.Text))
        {
            Assert.True(await applier.ApplyAsync(element));
            TextStyle applied = session.SelectedTitle!.Generator!.Style!;
            Assert.True(applied.ContentEquals(BuiltInStyles.Find(element.Reference)!), element.Reference);
            Assert.Equal(i++, TextStyleChoices.IndexOf(styles, applied));
        }

        // La primera aplicación creó el título; las demás lo cambiaron sin crear más.
        Assert.Equal(1, session.Sequence.VideoTracks.Sum(t => t.Clips.Count(c => c.Generator is not null)));
    });

    /// <summary>Un estilo de fábrica ya es la copia vigente: «Reaplicar estilo» no tiene paquete que releer (como «Texto»).</summary>
    [Fact]
    public void UnoDeFabricaNoTieneOrigenQueReaplicar() => UiThread.Run(async () =>
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        Assert.True(session.AddTitleAtPlayhead(BuiltInStyles.Find(BuiltInStyles.WordsReference)));
        var editor = new TitleEditor(session);

        Assert.Equal(ReapplyResult.NoSource, editor.ReapplyStyle(new FakeContentLoader()));
    });
}
