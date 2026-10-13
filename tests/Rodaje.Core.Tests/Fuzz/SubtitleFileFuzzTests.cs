using System.Text;
using Rodaje.Core.Editing;
using Rodaje.Core.Serialization;
using Rodaje.Tests.Fuzz;

namespace Rodaje.Core.Tests.Fuzz;

/// <summary>
/// Fuzz de los lectores de SubRip y WebVTT: con cualquier byte, <see cref="SubtitleFiles.Read"/> devuelve los subtítulos
/// que entendió y cuenta los que saltó; nunca lanza ni se cuelga, y lo leído se puede volver a escribir.
/// </summary>
public class SubtitleFileFuzzTests
{
    private const int Seed = 94_100_000;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    private const string SrtSeed =
        "1\r\n00:00:01,000 --> 00:00:02,500\r\n<i>Hola</i> {\\an8}a todos\r\n\r\n" +
        "2\r\n00:00:03,000 --> 00:00:04,000 X1:10 X2:20\r\nDos &amp; líneas\r\nsegunda\r\n\r\n" +
        "3\r\n01:59:59,999 --> 02:00:00,000\r\n<font color=\"#FF0000\">rojo</font>\r\n\r\n";

    private const string VttSeed =
        "WEBVTT - prueba\n\nNOTE comentario\nde dos líneas\n\nSTYLE\n::cue { color: yellow }\n\n" +
        "intro\n00:01.000 --> 00:02.000 align:start position:10%\n<v Ana>Hola <c.amarillo>mundo</c>\n\n" +
        "00:00:03.500 --> 00:00:05.000\n<00:00:04.000>karaoke &lt;3 &nbsp;\n\n";

    [Fact]
    public void Seeds_ReadAsExpected()
    {
        Assert.Equal(3, SubtitleFiles.Parse(SrtSeed).Cues.Count);
        Assert.Equal(2, SubtitleFiles.Parse(VttSeed).Cues.Count);
    }

    /// <summary>
    /// Cifras que no son ASCII (árabes, devanagari, de ancho completo) casan con <c>\d</c> en .NET y luego
    /// <see cref="long.Parse(string, IFormatProvider)"/> lanzaba <see cref="FormatException"/> en inglés (defecto hallado
    /// por este fuzz). Ahora el bloque no se entiende y se salta.
    /// </summary>
    [Theory]
    [InlineData("1\n٠٠:٠٠:٠١,٠٠٠ --> ٠٠:٠٠:٠٢,٠٠٠\nárabe\n")]
    [InlineData("1\n००:००:०१,००० --> ००:००:०२,०००\nदेवनागरी\n")]
    [InlineData("1\n００:００:０１,０００ --> ００:００:０２,０００\nancho completo\n")]
    [InlineData("WEBVTT\n\n٠١:٠٠.٠٠٠ --> ٠٢:٠٠.٠٠٠\nárabe\n")]
    [InlineData("1\n00:00:01,000 --> 00:00:0٢,000\nmezcla\n")]
    public void NonAsciiDigits_AreSkippedNotThrown(string text)
    {
        SubtitleFileContent content = SubtitleFiles.Parse(text);
        Assert.Empty(content.Cues);
        Assert.Equal(1, content.Skipped);
        Assert.False(SubtitleFiles.TryParseStamp("٠٠:٠١", out _));
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void MutatedFiles_NeverThrow()
    {
        string dir = Directory.CreateTempSubdirectory("rodaje-fuzz94-srt-").FullName;
        try
        {
            FuzzRunner.Run("subtítulos", Seed, cases: 4000, Budget, minimumCases: 1000, (seed, rng) =>
            {
                bool vtt = rng.Next(2) == 0;
                string text = MutateText(vtt ? VttSeed : SrtSeed, rng);
                byte[] bytes = Encode(text, rng);
                bytes = rng.Next(3) == 0 ? ProjectFileMutationFuzzTests.MutateBytes(bytes, rng) : bytes;
                string path = Path.Combine(dir, vtt ? "caso.vtt" : "caso.srt");
                SubtitleFileContent content;
                if (rng.Next(10) == 0)
                {
                    File.WriteAllBytes(path, bytes);
                    content = SubtitleFiles.Read(path);
                }
                else
                {
                    content = SubtitleFiles.Parse(SubtitleFiles.Decode(bytes));
                }

                Check(content);
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void HugeInputs_FinishQuickly()
    {
        string line = new('ñ', 10 * 1024 * 1024);
        Check(SubtitleFiles.Parse("1\n00:00:01,000 --> 00:00:02,000\n" + line + "\n"));
        Check(SubtitleFiles.Parse(line));
        Check(SubtitleFiles.Parse("WEBVTT\n\n" + string.Concat(Enumerable.Repeat("<a", 2 * 1024 * 1024))));
        Check(SubtitleFiles.Parse("1\n00:00:01,000 --> 00:00:02,000\n" + string.Concat(Enumerable.Repeat("{\\", 2 * 1024 * 1024))));
        Check(SubtitleFiles.Parse(string.Concat(Enumerable.Repeat("-->", 3 * 1024 * 1024))));
        Check(SubtitleFiles.Parse(new string('\n', 10 * 1024 * 1024)));

        var many = new StringBuilder();
        for (int i = 0; i < 100_000; i++)
        {
            many.Append("00:00:01,000 --> 00:00:02,000\nx\n\n");
        }

        Assert.Equal(100_000, SubtitleFiles.Parse(many.ToString()).Cues.Count);
        Check(SubtitleFiles.Parse("1\n9999:59:59,999 --> 99999:00:00,000\nx\n"));
        Check(SubtitleFiles.Parse("1\n9999:59:59,999 --> 9999:59:59,9999\nx\n"));
    }

    private static void Check(SubtitleFileContent content)
    {
        Assert.True(content.Skipped >= 0);
        foreach (SubtitleCue cue in content.Cues)
        {
            Assert.True(cue.Start.Ticks >= 0 && cue.End > cue.Start, $"{cue.Start} → {cue.End}");
            Assert.False(string.IsNullOrEmpty(cue.Text));
        }

        // Lo leído se puede volver a escribir en los dos formatos.
        _ = SubtitleFiles.Format(content.Cues, SubtitleFormat.Srt);
        _ = SubtitleFiles.Format(content.Cues, SubtitleFormat.WebVtt);
    }

    private static readonly string[] Pieces =
    [
        "-->", " --> ", "\n", "\r\n", "\r", "\n\n", ":", ",", ".", "<", ">", "</", "<i>", "{\\", "}", "&", "&#xFFFFFFFF;", "&#0;", "&#55296;",
        "&amp", "WEBVTT", "NOTE", "STYLE", "REGION", "\uFEFF", "\0", "\u2028", "٣", "०", "５", "99999", "-1", "00:00:00,000", "9999:59:59,999",
        "\uD800", "\uDC00", "😀", "\t", "\u00A0",
    ];

    private static string MutateText(string seed, Random rng)
    {
        var text = new StringBuilder(seed);
        int edits = 1 + rng.Next(5);
        for (int e = 0; e < edits; e++)
        {
            int at = rng.Next(text.Length + 1);
            switch (rng.Next(6))
            {
                case 0:
                    text.Insert(at, Pieces[rng.Next(Pieces.Length)]);
                    break;
                case 1 when at < text.Length:
                    text.Remove(at, Math.Min(text.Length - at, 1 + rng.Next(20)));
                    break;
                case 2:
                    text.Length = at;
                    break;
                case 3 when at < text.Length && char.IsAsciiDigit(text[at]):
                    // Otra cifra, a veces de otra escritura.
                    text[at] = rng.Next(3) == 0 ? "٠١٢٣٤٥٦٧٨٩०१२३४५६७८९０１２３４５６７８９"[rng.Next(30)] : (char)('0' + rng.Next(10));
                    break;
                case 4:
                    text.Insert(at, new string((char)rng.Next(0x20, 0x3000), 1 + rng.Next(3)));
                    break;
                default:
                    text.Insert(at, string.Concat(Enumerable.Repeat(Pieces[rng.Next(Pieces.Length)], 1 + rng.Next(200))));
                    break;
            }
        }

        return text.ToString();
    }

    private static byte[] Encode(string text, Random rng) => rng.Next(6) switch
    {
        0 => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
        1 => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
        2 => [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(text), .. (rng.Next(2) == 0 ? (byte[])[0x41] : [])],
        3 => Encoding.Latin1.GetBytes(text),
        _ => Encoding.UTF8.GetBytes(text),
    };
}
