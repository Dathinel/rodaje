using System.Text;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Tests.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Caza;

/// <summary>Contrato de «Importar o exportar SRT y VTT» (docs/guia/titulos-y-subtitulos.md).</summary>
public sealed class SubtitleFilesContractTests : IDisposable
{
    private const string SrtSample = "1\r\n00:00:01,000 --> 00:00:02,000\r\nCanción de cuna\r\n\r\n";
    private const string VttSample = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nCanción de cuna\n";

    private static readonly ClipGenerator Template = new() { Text = "", Style = ModelSamples.FullStyle() };

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rodaje-subtitulos-" + Guid.NewGuid().ToString("N"));

    public SubtitleFilesContractTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static Time Ms(long ms) => Time.FromTicks(ms * (Time.TicksPerSecond / 1000));

    public static TheoryData<string, string> Encodings() => new()
    {
        { "a.srt", "utf-8" },
        { "a.srt", "utf-8-bom" },
        { "a.srt", "utf-16le" },
        { "a.srt", "utf-16be" },
        { "a.srt", "latin1" },
        { "a.vtt", "utf-8" },
        { "a.vtt", "utf-8-bom" },
        { "a.vtt", "utf-16le" },
        { "a.vtt", "utf-16be" },
        { "a.vtt", "latin1" },
    };

    // «lee un `.srt` o un `.vtt` (UTF-8, UTF-16 o Latin 1)»
    [Theory]
    [MemberData(nameof(Encodings))]
    public void Reads_srt_and_vtt_in_utf8_utf16_and_latin1(string name, string encoding)
    {
        string text = name.EndsWith(".vtt", StringComparison.Ordinal) ? VttSample : SrtSample;
        byte[] bytes = encoding switch
        {
            "utf-8" => Encoding.UTF8.GetBytes(text),
            "utf-8-bom" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
            "utf-16le" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
            "utf-16be" => [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(text)],
            _ => Encoding.Latin1.GetBytes(text),
        };
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);

        SubtitleFileContent content = SubtitleFiles.Read(path);

        Assert.Equal(0, content.Skipped);
        Assert.Equal(new SubtitleCue(Ms(1000), Ms(2000), "Canción de cuna"), Assert.Single(content.Cues));
    }

    // «crea una pista de subtítulos nueva, con el nombre del archivo y el estilo de subtítulos por defecto»
    [Fact]
    public void Import_creates_a_new_subtitle_track_with_the_given_name_and_style()
    {
        TrimFixture f = new();
        int tracks = f.Sequence.VideoTracks.Count;
        SubtitleFileContent content = SubtitleFiles.Parse(SrtSample);

        new UndoStack(f.Project).Execute(Edits.ImportSubtitles(f.Sequence.Id, content.Cues, Template, Path.GetFileNameWithoutExtension("entrevista.srt")));

        Assert.Equal(tracks + 1, f.Sequence.VideoTracks.Count);
        Track track = f.Sequence.VideoTracks[^1];
        Assert.Equal(TrackRoles.Subtitles, track.Role);
        Assert.Equal("entrevista", track.Name);
        Clip clip = Assert.Single(track.Clips);
        Assert.Equal(Template.Style!.Size, clip.Generator!.Style!.Size);
        Assert.Equal(Template.Style.Font.Family, clip.Generator.Style.Font.Family);
    }

    // «Conserva los saltos de línea y quita las etiquetas de estilo (`<i>`, `{\an8}`…)»
    [Theory]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\n{\\an8}<i>Primera</i> línea\n<b>segunda</b> <font color=\"#ff0000\">línea</font>\n")]
    [InlineData("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<i>Primera</i> <c.amarillo>línea</c>\n<v Ana>segunda</v> <00:00:01.500>línea\n")]
    public void Keeps_line_breaks_and_strips_style_tags(string text)
    {
        SubtitleCue cue = Assert.Single(SubtitleFiles.Parse(text).Cues);

        Assert.Equal("Primera línea\nsegunda línea", cue.Text);
    }

    // «Los tiempos van al cuadro»
    [Fact]
    public void Imported_times_go_to_the_frame()
    {
        TrimFixture f = new();
        Rational rate = f.Sequence.FrameRate;
        SubtitleCue[] cues = [new(Ms(1013), Ms(2027), "uno"), new(Ms(3001), Ms(4999), "dos")];

        new UndoStack(f.Project).Execute(Edits.ImportSubtitles(f.Sequence.Id, cues, Template, "pista"));

        Track track = f.Sequence.VideoTracks[^1];
        Assert.Equal([Ms(1000), Ms(3000)], track.Clips.Select(c => c.Start));
        Assert.Equal([Ms(2040), Ms(5000)], track.Clips.Select(c => c.Start + c.Duration));
        Assert.All(track.Clips, c =>
        {
            Assert.Equal(c.Start, c.Start.SnapToFrame(rate));
            Assert.Equal(c.Duration, c.Duration.SnapToFrame(rate));
        });
    }

    // «si dos se pisan, el primero termina donde empieza el segundo»
    [Fact]
    public void When_two_overlap_the_first_ends_where_the_second_starts()
    {
        TrimFixture f = new();
        SubtitleCue[] cues = [new(Ms(1000), Ms(3000), "primero"), new(Ms(2000), Ms(4000), "segundo"), new(Ms(3600), Ms(6000), "tercero")];

        new UndoStack(f.Project).Execute(Edits.ImportSubtitles(f.Sequence.Id, cues, Template, "pista"));

        Clip[] clips = [.. f.Sequence.VideoTracks[^1].Clips.OrderBy(c => c.Start)];
        Assert.Equal(["primero", "segundo", "tercero"], clips.Select(c => c.Generator!.Text));
        Assert.Equal([Ms(1000), Ms(2000), Ms(3600)], clips.Select(c => c.Start));
        Assert.Equal(clips[1].Start, clips[0].Start + clips[0].Duration);
        Assert.Equal(clips[2].Start, clips[1].Start + clips[1].Duration);
        Assert.Equal(Ms(6000), clips[2].Start + clips[2].Duration);
    }

    // «Es un solo paso del historial: Ctrl+Z la quita entera.»
    [Fact]
    public void Import_is_one_history_step_and_undo_removes_it_whole()
    {
        TrimFixture f = new();
        var stack = new UndoStack(f.Project);
        string before = string.Join('|', f.Sequence.VideoTracks.Select(t => t.Id + ":" + t.Clips.Count));
        SubtitleFileContent content = SubtitleFiles.Parse("1\n00:00:00,000 --> 00:00:01,000\nUno\n\n2\n00:00:02,000 --> 00:00:03,000\nDos\n");

        stack.Execute(Edits.ImportSubtitles(f.Sequence.Id, content.Cues, Template, "pista"));
        Assert.Equal(2, f.Sequence.VideoTracks[^1].Clips.Count);
        stack.Undo();

        Assert.Equal(before, string.Join('|', f.Sequence.VideoTracks.Select(t => t.Id + ":" + t.Clips.Count)));
        Assert.False(stack.CanUndo);
    }

    // «guarda la pista como `.srt` o `.vtt` en UTF-8, al milisegundo»
    [Theory]
    [InlineData("salida.srt", "00:00:01,235 --> 00:00:02,000")]
    [InlineData("salida.vtt", "00:00:01.235 --> 00:00:02.000")]
    public void Export_writes_srt_or_vtt_in_utf8_to_the_millisecond(string name, string stamps)
    {
        Time start = Ms(1234) + Time.FromTicks(Time.TicksPerSecond / 1000 * 6 / 10);
        SubtitleCue[] cues = [new(start, Ms(2000), "Señoría, ¿qué tal?")];
        string path = Path.Combine(_folder, name);

        SubtitleFiles.Write(path, cues);

        byte[] bytes = File.ReadAllBytes(path);
        string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        Assert.Contains(stamps, text, StringComparison.Ordinal);
        Assert.Contains("Señoría, ¿qué tal?", text, StringComparison.Ordinal);
        Assert.Equal(name.EndsWith(".vtt", StringComparison.Ordinal), text.StartsWith("WEBVTT", StringComparison.Ordinal));
        Assert.Equal(new SubtitleCue(Ms(1235), Ms(2000), "Señoría, ¿qué tal?"), Assert.Single(SubtitleFiles.Read(path).Cues));
    }
}
