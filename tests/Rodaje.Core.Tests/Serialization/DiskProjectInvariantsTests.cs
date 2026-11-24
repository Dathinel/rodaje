using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Rodaje.Core.Commands;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Serialization;

public sealed class DiskProjectInvariantsTests : IDisposable
{
    private readonly string _folder = Scratch();

    public DiskProjectInvariantsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Rich_project_is_exact_and_a_second_disk_save_is_byte_identical(int variant)
    {
        Project project = Rich(variant);
        Assert.Empty(ModelValidator.Validate(project));
        string first = Path.Combine(_folder, "Edición ñ original.rodaje");
        string second = Path.Combine(_folder, "Edición ñ cargada.rodaje");
        ProjectSerializer.Save(project, first);
        Project loaded = ProjectSerializer.Load(first);
        Assert.Equal(ProjectSerializer.Serialize(project), ProjectSerializer.Serialize(loaded));
        ProjectSerializer.Save(loaded, second);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Theory]
    [InlineData("es-CO")]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public void Disk_bytes_are_identical_under_every_thread_culture(string culture)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo originalUi = CultureInfo.CurrentUICulture;
        try
        {
            Project project = Rich(2);
            string invariant = Path.Combine(_folder, "invariante.rodaje");
            string localized = Path.Combine(_folder, "localizado.rodaje");
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            ProjectSerializer.Save(project, invariant);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            ProjectSerializer.Save(ProjectSerializer.Load(invariant), localized);
            Assert.Equal(File.ReadAllBytes(invariant), File.ReadAllBytes(localized));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }

    [Theory]
    [InlineData("vacío")]
    [InlineData("truncado")]
    [InlineData("versión futura")]
    [InlineData("duración de texto")]
    [InlineData("pistas como objeto")]
    [InlineData("ancho como lista")]
    public void Broken_disk_files_fail_with_a_project_error_and_keep_the_original_bytes(string mutation)
    {
        JsonObject root = JsonNode.Parse(ProjectSerializer.Serialize(Rich(1)))!.AsObject();
        JsonNode sequence = root["sequences"]![0]!;
        switch (mutation)
        {
            case "versión futura": root["schemaVersion"] = Project.CurrentSchemaVersion + 1; break;
            case "duración de texto": root["media"]![0]!["duration"] = "mucho"; break;
            case "pistas como objeto": sequence["videoTracks"] = new JsonObject(); break;
            case "ancho como lista": sequence["width"] = new JsonArray(1920); break;
        }

        string json = root.ToJsonString();
        if (mutation == "vacío")
        {
            json = "";
        }
        else if (mutation == "truncado")
        {
            json = json[..(json.Length / 2)];
        }

        string path = Path.Combine(_folder, "Dañado ñ.rodaje");
        File.WriteAllText(path, json);
        byte[] original = File.ReadAllBytes(path);
        ProjectFormatException error = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Load(path));
        Assert.Contains("Dañado ñ.rodaje", error.Message, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("raíz")]
    [InlineData("clip")]
    [InlineData("efecto")]
    public void Unknown_fields_are_ignored_without_changing_known_data(string scope)
    {
        Project expected = Rich(1);
        string canonical = ProjectSerializer.Serialize(expected);
        JsonObject root = JsonNode.Parse(canonical)!.AsObject();
        JsonObject clip = root["sequences"]![0]!["videoTracks"]![0]!["clips"]![0]!.AsObject();
        JsonObject target = scope switch { "clip" => clip, "efecto" => clip["effects"]![0]!.AsObject(), _ => root };
        target["campoDelFuturo"] = new JsonObject { ["ñ"] = new JsonArray(1, 2, 3) };
        string path = Path.Combine(_folder, "extensible.rodaje");
        File.WriteAllText(path, root.ToJsonString());
        Assert.Equal(canonical, ProjectSerializer.Serialize(ProjectSerializer.Load(path)));
    }

    [Theory]
    [InlineData("material/toma con espacios.mp4")]
    [InlineData("../música/niñez y canción.wav")]
    [InlineData(@"Z:\medio que no existe\edición ñ.mp4")]
    public void Missing_and_relative_media_paths_survive_disk_round_trip_verbatim(string mediaPath)
    {
        Project project = Rich(1);
        project.Media[0].Path = mediaPath;
        string path = Path.Combine(_folder, "rutas.rodaje");
        ProjectSerializer.Save(project, path);
        Assert.Equal(mediaPath, ProjectSerializer.Load(path).Media[0].Path);
        Assert.Equal(ProjectSerializer.Serialize(project), ProjectSerializer.Serialize(ProjectSerializer.Load(path)));
    }

    [Theory]
    [InlineData("antes de reemplazar")]
    [InlineData("temporal bloqueado")]
    [InlineData("serialización")]
    public void Interrupted_save_preserves_the_previous_file_exactly(string interruption)
    {
        Project project = Rich(1);
        string path = Path.Combine(_folder, "trabajo anterior.rodaje");
        ProjectSerializer.Save(project, path);
        byte[] previous = File.ReadAllBytes(path);
        project.Name = "Versión nueva";
        if (interruption == "temporal bloqueado")
        {
            using (var locked = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Assert.Throws<IOException>(() => ProjectSerializer.Save(project, path, new NoRetries()));
                Assert.Equal(previous, File.ReadAllBytes(path));
            }
        }
        else if (interruption == "serialización")
        {
            project.Sequences[0].MasterGainDb = double.NaN;
            Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Save(project, path));
        }
        else
        {
            Assert.Throws<IOException>(() => ProjectSerializer.Save(project, path, new BrokenReplacement()));
            Assert.False(File.Exists(path + ".tmp"));
        }

        Assert.Equal(previous, File.ReadAllBytes(path));
        Assert.Equal("Vacaciones en Cartagena — edición ñ", ProjectSerializer.Load(path).Name);
    }

    private class NoRetries : ProjectSerializer.SaveFileSystem
    {
        public override IReadOnlyList<TimeSpan> RetryDelays => [];
    }

    private sealed class BrokenReplacement : NoRetries
    {
        public override void Replace(string source, string destination, string backup)
        {
            Assert.True(File.Exists(source));
            Assert.NotEmpty(File.ReadAllBytes(source));
            throw new IOException("Interrupción de escritura inyectada.");
        }
    }

    private static Project Rich(int variant)
    {
        Project project = SerializationSamples.Rich();
        Sequence sequence = project.Sequences[0];
        sequence.ColorSpace = (SequenceColorSpace)(variant % 3);
        sequence.MasterGainDb = -1.25;
        sequence.MasterEffects.Add(new EffectInstance { Id = "master-effect", Type = "paquete.mezcla",
            Options = new() { ["receta"] = "Voz y canción ñ" }, Parameters = new() { ["mezcla"] = new Parameter { Value = 0.375 } } });
        sequence.AudioBuses.Add(new AudioBus { Id = "bus", Name = "Voces ñ", Muted = true,
            Effects = [new EffectInstance { Id = "bus-effect", Type = "paquete.desconocido", Parameters = new() { ["valor"] = new Parameter { Value = 0.125 } } }] });
        sequence.AudioTracks[1].Output = "bus";
        sequence.AudioTracks[1].Armed = true;
        sequence.AudioTracks[1].Solo = true;
        sequence.AudioTracks[1].Height = 120;
        sequence.AudioTracks[0].Sends.Add(new AudioSend { BusId = "bus", GainDb = -3.75, PreFader = true });
        sequence.Markers.Add(new Marker { Id = "marker", Time = Time.FromSeconds(1.25), Name = "Señal", Note = "Canción", Color = "#123ABC" });
        sequence.Regions.Add(new Region { Id = "region", Start = Time.Zero, End = Time.FromSeconds(3.5), Name = "Región ñ" });
        Track track = sequence.VideoTracks[0];
        track.Clips[0].Color = "#ABCDEF";
        track.Clips[0].GroupId = "group";
        track.Clips[1].GroupId = "group";
        var ids = new SequentialIdGenerator();
        for (int i = 0; i < 20; i++)
        {
            _ = ids.NewId("reserved");
        }
        // Las operaciones construyen velocidades y transiciones válidas, con sus campos dependientes.
        Edits.SetSpeed(sequence.Id, "clip-2", new Rational(variant, 2), ids: ids).Apply(project);
        var add = Edits.AppendMedia(sequence.Id, "media-video", Time.FromSeconds(20), Time.FromSeconds(25), track.Id, ids: ids);
        add.Apply(project);
        var next = Edits.AppendMedia(sequence.Id, "media-video", Time.FromSeconds(30), Time.FromSeconds(35), track.Id, ids: ids);
        next.Apply(project);
        Edits.AddTransition(sequence.Id, add.CreatedIds[0], BuiltInTransitions.Crossfade, Time.FromSeconds(0.5), ids).Apply(project);
        var mask = Edits.AddMask(sequence.Id, "clip-1", MaskShape.Ellipse, ids: ids);
        mask.Apply(project);
        Edits.ConvertMaskToBezier(sequence.Id, "clip-1", mask.CreatedIds[0], Time.FromSeconds(1), 16.0 / 9, ids).Apply(project);
        Edits.SetMaskShapeKey(sequence.Id, "clip-1", mask.CreatedIds[0], Time.FromSeconds(2), ids).Apply(project);
        sequence.VideoTracks.Add(new Track { Id = "retime-track", Kind = TrackKind.Video, Clips =
            [new Clip { Id = "ramp-clip", MediaId = "media-video", SourceIn = Time.FromSeconds(10), SourceOut = Time.FromSeconds(15) },
                new Clip { Id = "reverse-clip", MediaId = "media-video", Start = Time.FromSeconds(10), SourceIn = Time.FromSeconds(20), SourceOut = Time.FromSeconds(25) },
                new Clip { Id = "freeze-clip", MediaId = "media-video", Start = Time.FromSeconds(20), SourceIn = Time.FromSeconds(30), SourceOut = Time.FromSeconds(35) }] });
        Edits.SetCurve(sequence.Id, "ramp-clip", new SpeedCurve { Span = Time.FromSeconds(5),
            Points = [new SpeedPoint(0, 50), new SpeedPoint(5000, 100), new SpeedPoint(10000, 150)] }, ids: ids).Apply(project);
        Edits.SetReverse(sequence.Id, "reverse-clip", true, ids).Apply(project);
        Edits.FreezeFrame(sequence.Id, "freeze-clip", Time.FromSeconds(21), Time.FromSeconds(1), ids).Apply(project);
        sequence.VideoTracks.Add(new Track { Id = "titles", Kind = TrackKind.Video, Role = TrackRoles.Subtitles,
            Clips = [new Clip { Id = "title", SourceOut = Time.FromSeconds(2), Generator = new ClipGenerator
                { Text = "Edición de niñez", Style = new TextStyle(), Words =
                    [new GeneratorWord { Text = "Edición", Start = Time.Zero, End = Time.FromSeconds(0.5) },
                        new GeneratorWord { Text = "de", Start = Time.FromSeconds(0.5), End = Time.FromSeconds(1) },
                        new GeneratorWord { Text = "niñez", Start = Time.FromSeconds(1), End = Time.FromSeconds(2) }] } }] });
        sequence.VideoTracks.Add(new Track { Id = "compound-track", Kind = TrackKind.Video,
            Clips = [new Clip { Id = "compound", SequenceId = project.Sequences[1].Id, SourceOut = Time.FromSeconds(1) }] });
        return project;
    }

    private static string Scratch([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, ".disk-tests-" + Guid.NewGuid().ToString("N"));
}
