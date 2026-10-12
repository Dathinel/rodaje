using System.Text;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Tests.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>SRT y WebVTT: lectura tolerante, escritura estricta y la importación como un paso del historial.</summary>
public class SubtitleFilesTests
{
    private static readonly ClipGenerator Template = new() { Text = "", Style = ModelSamples.FullStyle() };

    private static Time Ms(long ms) => Time.FromTicks(ms * (Time.TicksPerSecond / 1000));

    [Fact]
    public void Srt_with_accents_line_breaks_and_tags_is_read_exactly()
    {
        const string srt = "1\r\n00:00:01,000 --> 00:00:03,500\r\n<i>¿Qué tal, señoría?</i>\r\nÁrbol, pingüino y café\r\n\r\n"
            + "2\r\n00:00:04,250 --> 00:00:05,000\r\n{\\an8}Arriba &amp; abajo\r\n";
        SubtitleFileContent content = SubtitleFiles.Parse(srt);

        Assert.Equal(0, content.Skipped);
        Assert.Equal(2, content.Cues.Count);
        Assert.Equal("¿Qué tal, señoría?\nÁrbol, pingüino y café", content.Cues[0].Text);
        Assert.Equal(Ms(1000), content.Cues[0].Start);
        Assert.Equal(Ms(3500), content.Cues[0].End);
        Assert.Equal("Arriba & abajo", content.Cues[1].Text);
        Assert.Equal(Ms(4250), content.Cues[1].Start);
    }

    [Fact]
    public void Vtt_skips_header_notes_and_styles_and_reads_short_stamps_and_settings()
    {
        const string vtt = "WEBVTT - prueba\nKind: captions\n\nNOTE esto no sale\n\nSTYLE\n::cue { color: red }\n\n"
            + "intro\n00:01.000 --> 00:02.500 align:start line:10%\n<v Ana>Hola, <b>niño</b> &lt;3</v>\n\n"
            + "01:00:00.000 --> 01:00:01.001\nÚltimo\n";
        SubtitleFileContent content = SubtitleFiles.Parse(vtt);

        Assert.Equal(2, content.Cues.Count);
        Assert.Equal("Hola, niño <3", content.Cues[0].Text);
        Assert.Equal(Ms(1000), content.Cues[0].Start);
        Assert.Equal(Ms(2500), content.Cues[0].End);
        Assert.Equal(Ms(3_600_000), content.Cues[1].Start);
        Assert.Equal(Ms(3_601_001), content.Cues[1].End);
    }

    [Fact]
    public void Broken_blocks_are_counted_and_skipped()
    {
        const string srt = "1\n00:00:01,000 --> 00:00:00,500\nAl revés\n\n2\nsin tiempos\n\n3\n00:00:02,000 --> 00:00:03,000\n\n"
            + "4\n00:00:61,000 --> 00:00:62,000\nSegundos imposibles\n\n5\n00:00:05,000 --> 00:00:06,000\nBien\n";
        SubtitleFileContent content = SubtitleFiles.Parse(srt);

        Assert.Equal("Bien", Assert.Single(content.Cues).Text);
        Assert.Equal(4, content.Skipped);
    }

    [Theory]
    [InlineData(SubtitleFormat.Srt)]
    [InlineData(SubtitleFormat.WebVtt)]
    public void Writing_and_reading_back_keeps_text_and_milliseconds(SubtitleFormat format)
    {
        SubtitleCue[] cues =
        [
            new(Time.Zero, Ms(1), "Empieza en cero\ny dura un milisegundo"),
            new(Ms(1500), Ms(2750), "Ñandú & «comillas» 3 < 4"),
            new(Ms(359_999_000), Ms(360_000_000), "Casi cien horas"),
        ];
        string text = SubtitleFiles.Format(cues, format);
        SubtitleFileContent back = SubtitleFiles.Parse(text);

        Assert.Equal(cues, back.Cues);
        Assert.Contains(format == SubtitleFormat.Srt ? "99:59:59,000 --> 100:00:00,000" : "99:59:59.000 --> 100:00:00.000", text, StringComparison.Ordinal);
        if (format == SubtitleFormat.Srt)
        {
            Assert.StartsWith("1\r\n00:00:00,000 --> 00:00:00,001\r\n", text, StringComparison.Ordinal);
        }
        else
        {
            Assert.StartsWith("WEBVTT\n\n00:00:00.000", text, StringComparison.Ordinal);
            Assert.Contains("&amp; «comillas» 3 &lt; 4", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Writing_drops_blank_lines_and_arrows_that_would_break_the_file()
    {
        string text = SubtitleFiles.Format([new(Ms(0), Ms(0), "uno\n\n  \ndos --> tres")], SubtitleFormat.Srt);
        Assert.Equal("1\r\n00:00:00,000 --> 00:00:00,001\r\nuno\r\ndos -> tres\r\n\r\n", text);
    }

    [Fact]
    public void Decoding_reads_utf8_utf16_and_falls_back_to_latin1()
    {
        const string sample = "00:00:01,000 --> 00:00:02,000\nCanción\n";
        Assert.Equal("Canción", SubtitleFiles.Parse(SubtitleFiles.Decode([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sample)])).Cues[0].Text);
        Assert.Equal("Canción", SubtitleFiles.Parse(SubtitleFiles.Decode([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(sample)])).Cues[0].Text);
        Assert.Equal("Canción", SubtitleFiles.Parse(SubtitleFiles.Decode(Encoding.Latin1.GetBytes(sample))).Cues[0].Text);
        Assert.Equal("Canción", SubtitleFiles.Parse(SubtitleFiles.Decode(Encoding.UTF8.GetBytes(sample))).Cues[0].Text);
    }

    [Fact]
    public void Fitting_to_frames_removes_overlaps_and_keeps_one_frame_at_least()
    {
        Time frame = Time.FromFrames(1, Rational.Fps25);
        IReadOnlyList<SubtitleCue> fitted = SubtitleEdits.FitToFrames(
        [
            new(Ms(1000), Ms(3000), "solapa"),
            new(Ms(2000), Ms(2010), "corto"),
            new(Ms(2000), Ms(2500), "mismo inicio"),
            new(Ms(9000), Ms(9001), "un instante"),
            new(Ms(9500), Ms(9400), "al revés"),
        ], Rational.Fps25);

        Assert.Equal(["solapa", "corto", "mismo inicio", "un instante"], fitted.Select(c => c.Text));
        Assert.Equal(Ms(2000), fitted[0].End);
        Assert.Equal(Ms(2000), fitted[1].Start);
        Assert.Equal(Ms(2000) + frame, fitted[1].End);
        Assert.Equal(Ms(2000) + frame, fitted[2].Start);
        Assert.Equal(frame, fitted[3].End - fitted[3].Start);
        for (int i = 1; i < fitted.Count; i++)
        {
            Assert.True(fitted[i].Start >= fitted[i - 1].End);
        }
    }

    [Fact]
    public void Import_is_one_undo_step_on_a_new_subtitle_track_with_the_template_style()
    {
        TrimFixture f = new();
        var stack = new UndoStack(f.Project);
        int tracks = f.Sequence.VideoTracks.Count;
        SubtitleFileContent content = SubtitleFiles.Parse("1\n00:00:00,000 --> 00:00:01,000\nPrimera línea\nsegunda\n\n2\n00:00:02,000 --> 00:00:04,000\nAdiós\n");
        SequenceEditCommand command = Edits.ImportSubtitles(f.Sequence.Id, content.Cues, Template, "entrevista");
        stack.Execute(command);

        Track track = f.Sequence.VideoTracks[^1];
        Assert.Equal(TrackRoles.Subtitles, track.Role);
        Assert.Equal("entrevista", track.Name);
        Assert.Equal(["Primera línea\nsegunda", "Adiós"], track.Clips.Select(c => c.Generator!.Text));
        Assert.All(track.Clips, c => Assert.Equal(Template.Style!.Size, c.Generator!.Style!.Size));
        Assert.Equal(content.Cues, SubtitleEdits.CuesOf(track));
        Assert.Equal(3, command.CreatedIds.Count);

        stack.Undo();
        Assert.Equal(tracks, f.Sequence.VideoTracks.Count);
        Assert.Throws<EditException>(() => SubtitleEdits.ImportCues(f.Ctx, [], Template, "vacío"));
    }
}
