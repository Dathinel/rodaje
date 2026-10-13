using System.Diagnostics;
using Rodaje.App.Views.Library;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.Library;

/// <summary>Valoración, marcas, etiquetas, subclips, colecciones inteligentes, metadatos, repetidos y rendimiento.</summary>
public sealed class LibraryProTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static MediaRef Video(string id, string name, int height = 1080, double seconds = 10, double fps = 30) => new()
    {
        Id = id,
        Name = name,
        Path = $@"C:\medios\{name}.mp4",
        Duration = Time.FromSeconds(seconds),
        HasVideo = true,
        HasAudio = true,
        Width = height * 16 / 9,
        Height = height,
        FrameRate = fps == 30 ? Rational.Fps30 : fps == 25 ? Rational.Fps25 : Rational.Ntsc30,
        SampleRate = 48000,
        AudioChannels = 2,
    };

    private static MediaRef Sound(string id, string name) => new()
    {
        Id = id, Name = name, Path = $@"C:\medios\{name}.wav", Duration = Time.FromSeconds(30), HasAudio = true, SampleRate = 48000, AudioChannels = 2,
    };

    [Fact]
    public void Favorito_y_rechazado_se_excluyen_y_la_valoracion_se_acota()
    {
        var org = new LibraryOrganization();

        org.SetMark("a", MediaMark.Rejected);
        org.SetFavorite("a", true);
        Assert.Equal(MediaMark.Favorite, org.MarkOf("a"));
        org.SetMark("a", MediaMark.Rejected);
        Assert.False(org.IsFavorite("a"));
        org.SetRating("a", 9);
        Assert.Equal(5, org.RatingOf("a"));
        org.SetRating("a", 0);
        Assert.Empty(org.Ratings);
    }

    [Fact]
    public void Las_etiquetas_se_limpian_sin_repetir_y_se_ponen_a_varios()
    {
        var org = new LibraryOrganization();

        org.SetTags("a", LibraryOrganization.ParseTags(" boda, Exteriores ; BODA,, "));
        Assert.Equal(["boda", "Exteriores"], org.TagsOf("a"));
        org.ToggleTag(["a", "b"], "Montaña", on: true);
        org.ToggleTag(["a"], "exteriores", on: false);
        Assert.Equal(["boda", "Montaña"], org.TagsOf("a"));
        Assert.Equal(["boda", "Montaña"], org.AllTags());
    }

    [Fact]
    public void Filtrar_por_valoracion_marca_etiqueta_y_rechazados_mira_toda_la_biblioteca()
    {
        var org = new LibraryOrganization();
        LibraryFolder folder = org.CreateFolder(null, "Día 1");
        MediaRef[] media = [Video("a", "plano"), Video("b", "detalle"), Sound("c", "ambiente")];
        org.MoveMedia(["a"], folder.Id);
        org.SetRating("a", 4);
        org.SetRating("b", 2);
        org.SetMark("b", MediaMark.Rejected);
        org.SetTags("c", ["sala"]);
        MediaFacts[] facts = [.. media.Select(m => new MediaFacts(m, org))];

        string[] Ids(LibraryQuery q) => [.. LibraryFilter.Apply(facts, org, q, Now).Select(f => f.Media.Id)];
        Assert.Equal(["a"], Ids(new LibraryQuery(MinRating: 3)));
        Assert.Equal(["b"], Ids(new LibraryQuery(Mark: MediaMark.Rejected)));
        Assert.Equal(["c"], Ids(new LibraryQuery(Tag: "SALA")));
        Assert.Equal(["c"], Ids(new LibraryQuery(HideRejected: true)));
        Assert.Equal(["c"], Ids(new LibraryQuery(Text: "sala")));
    }

    [Fact]
    public void Los_metadatos_se_buscan_y_las_colecciones_se_recalculan()
    {
        var org = new LibraryOrganization();
        MediaRef[] media = [Video("a", "toma", 2160, 120, 25), Video("b", "toma2", 720, 5), Sound("c", "musica")];
        MediaFacts[] facts =
        [
            new(media[0], org, "H.265 · AAC", "Sony ILCE-7M4", Now.AddDays(-2), offline: false, used: true),
            new(media[1], org, "H.264 · AAC", "Apple iPhone 15 Pro", Now.AddDays(-40), offline: true),
            new(media[2], org, "PCM", null, null),
        ];
        org.SetRating("a", 5);

        string[] Search(string text) => [.. LibraryFilter.Apply(facts, org, new LibraryQuery(Text: text), Now).Select(f => f.Media.Id)];
        Assert.Equal(["a"], Search("sony"));
        Assert.Equal(["b"], Search("iphone 1280×720"));
        Assert.Equal(["a"], Search("2026-10-10"));
        Assert.Equal(["a", "b"], Search("aac"));

        SmartBin recent = org.SaveCollection(new SmartBin("", "  4K recientes ", [new(SmartField.MinHeight, "2160"), new(SmartField.WithinDays, "7")]));
        SmartBin problems = org.SaveCollection(new SmartBin("", "Revisar", [new(SmartField.Offline, "true"), new(SmartField.Kind, nameof(LibraryKind.Audio))], MatchAll: false));
        SmartBin used = org.SaveCollection(new SmartBin("", "Sin usar", [new(SmartField.Used, "false"), new(SmartField.Codec, "264")]));
        Assert.Equal("4K recientes", recent.Name);
        string[] In(SmartBin c) => [.. LibraryFilter.Apply(facts, org, new LibraryQuery(CollectionId: c.Id), Now).Select(f => f.Media.Id)];
        Assert.Equal(["a"], In(recent));
        Assert.Equal(["b", "c"], In(problems));
        Assert.Equal(["b"], In(used));

        // Una colección es una regla, no una lista: lo que cambia entra o sale solo.
        recent = org.SaveCollection(recent with { Rules = [new(SmartField.MinRating, "5")] });
        Assert.Equal(["a"], In(recent));
        org.SetRating("b", 5);
        Assert.Equal(["a", "b"], In(recent));
        Assert.Equal("Al menos 5 estrellas", recent.Rules[0].Describe());
    }

    [Fact]
    public void Subclips_colecciones_y_multicamaras_se_guardan_y_se_leen()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rodaje-biblioteca-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LibraryOrganizationStore(folder);
            var org = new LibraryOrganization();
            org.SetRating("a", 3);
            org.SetMark("b", MediaMark.Rejected);
            org.SetTags("a", ["boda", "exterior"]);
            Subclip sub = org.AddSubclip("a", "El sí", Time.FromSeconds(2), Time.FromSeconds(5));
            org.SaveCollection(new SmartBin("", "Buenas", [new(SmartField.MinRating, "3"), new(SmartField.Text, "boda")]));
            org.AddMulticam("Boda", [new MulticamAngleEntry("a", Time.Zero), new MulticamAngleEntry("b", Time.FromSamples(-12345, 48000))]);

            store.Save("p", org);
            LibraryOrganization read = new LibraryOrganizationStore(folder).Load("p");

            Assert.Equal(3, read.RatingOf("a"));
            Assert.True(read.IsRejected("b"));
            Assert.Equal(["boda", "exterior"], read.TagsOf("a"));
            Assert.Equal(sub, Assert.Single(read.Subclips));
            SmartBin collection = Assert.Single(read.Collections);
            Assert.Equal(["Al menos 3 estrellas", "Contiene «boda»"], collection.Rules.Select(r => r.Describe()));
            MulticamGroup group = Assert.Single(read.Multicams);
            Assert.Equal(Time.FromSamples(-12345, 48000), group.Angles[1].Offset);
            Assert.Same(group, read.MulticamOf("b"));

            // Si se quita un medio del proyecto, sus marcas y su multicámara de dos ángulos se olvidan.
            read.Prune(["a"]);
            Assert.False(read.IsRejected("b"));
            Assert.Empty(read.Multicams);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Un_archivo_viejo_sin_marcas_se_lee_igual()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rodaje-biblioteca-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "p.json"), """{ "version": 1, "folders": [], "media": {}, "favorites": ["a"] }""");
            LibraryOrganization read = new LibraryOrganizationStore(folder).Load("p");
            Assert.Equal(MediaMark.Favorite, read.MarkOf("a"));
            Assert.Empty(read.Subclips);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Los_repetidos_se_encuentran_por_ruta_y_por_contenido()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rodaje-repetidos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            byte[] content = new byte[300_000];
            new Random(5).NextBytes(content);
            byte[] other = (byte[])content.Clone();
            other[150_000] ^= 0xFF;
            string a = Path.Combine(folder, "toma.mp4");
            string copy = Path.Combine(folder, "toma (copia).mp4");
            string different = Path.Combine(folder, "otra.mp4");
            File.WriteAllBytes(a, content);
            File.WriteAllBytes(copy, content);
            File.WriteAllBytes(different, other);

            IReadOnlyList<IReadOnlyList<string>> groups = MediaDuplicates.Find(
            [
                new("m1", a), new("m2", copy), new("m3", different), new("m4", a.ToUpperInvariant()), new("m5", Path.Combine(folder, "falta.mp4")),
            ]);

            Assert.Equal(["m1", "m4", "m2"], Assert.Single(groups));
            Assert.Equal(["m5"], MediaDuplicates.Offline([new("m1", a), new("m5", Path.Combine(folder, "falta.mp4"))]));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Filtrar_2000_medios_tarda_menos_de_50_ms()
    {
        var org = new LibraryOrganization();
        var random = new Random(3);
        string[] words = ["playa", "montaña", "boda", "entrevista", "dron", "cocina", "noche", "ciudad"];
        LibraryFolder[] folders = [.. Enumerable.Range(0, 20).Select(i => org.CreateFolder(null, $"Carpeta {i}"))];
        var facts = new List<MediaFacts>(2000);
        for (int i = 0; i < 2000; i++)
        {
            MediaRef m = i % 5 == 0 ? Sound($"m{i}", $"{words[i % 8]} {i}") : Video($"m{i}", $"{words[i % 8]} toma {i}", i % 3 == 0 ? 2160 : 1080, 1 + random.Next(600));
            org.MoveMedia([m.Id], folders[i % 20].Id);
            org.SetRating(m.Id, i % 6);
            org.SetTags(m.Id, [words[(i + 3) % 8], $"cliente {i % 40}"]);
            if (i % 9 == 0)
            {
                org.SetMark(m.Id, MediaMark.Rejected);
            }

            facts.Add(new MediaFacts(m, org, i % 2 == 0 ? "H.264 · AAC" : "H.265 · AAC", i % 4 == 0 ? "Sony ILCE-7M4" : "Canon EOS R6", Now.AddDays(-(i % 90)), used: i % 7 == 0));
        }

        SmartBin collection = org.SaveCollection(new SmartBin("", "Buenas 4K", [new(SmartField.MinRating, "3"), new(SmartField.MinHeight, "2160"), new(SmartField.Camera, "sony")]));
        LibraryQuery[] queries =
        [
            new(Text: "montana toma"), new(MinRating: 4, HideRejected: true), new(Tag: "cliente 7"), new(CollectionId: collection.Id),
            new(folders[3].Id, Kind: LibraryKind.Video), new(Text: "sony 2026-09"),
        ];
        foreach (LibraryQuery query in queries)
        {
            LibraryFilter.Apply(facts, org, query, Now);
        }

        var times = new List<double>();
        foreach (LibraryQuery query in queries)
        {
            for (int k = 0; k < 5; k++)
            {
                var watch = Stopwatch.StartNew();
                int count = LibraryFilter.Apply(facts, org, query, Now).Count;
                times.Add(watch.Elapsed.TotalMilliseconds);
                Assert.True(count >= 0);
            }
        }

        double worst = times.Max();
        output.WriteLine($"2000 medios: mediana {times.Order().ElementAt(times.Count / 2):0.00} ms, peor {worst:0.00} ms");
        Assert.True(worst < 50, $"filtrar tardó {worst:0.0} ms");
    }
}
