using CsCheck;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Xunit.Abstractions;
using static Rodaje.Core.Tests.Properties.V1Support;

namespace Rodaje.Core.Tests.Properties;

/// <summary>
/// Subtítulos por palabra (<see cref="ClipGenerator.Words"/>): dividir, unir y corregir conservan las palabras y el texto
/// (salvo los saltos de línea, que se reparten de nuevo), el lugar de cada palabra en la línea de tiempo y el deshacer.
/// </summary>
/// <remarks>Para reproducir un fallo, pasar la semilla que reporta CsCheck en <c>seed</c> o en <c>CsCheck_Seed</c>.</remarks>
public class SubtitleWordPropertiesTests(ITestOutputHelper output)
{
    private const string Seq = "seq";

    /// <summary>Letras de las palabras: acentos, eñes, signos, dígitos y un par de caracteres fuera del plano básico.</summary>
    private const string Alphabet = "aeiounsáéíóúñüÁÑ¿?¡!,.;:'-0123456789日本😀";

    private static readonly Gen<string> GenWord =
        Gen.Int[1, 9].SelectMany(n => Gen.Int[0, Alphabet.Length - 1].Array[n].Select(ix =>
        {
            string w = string.Concat(ix.Select(i => Alphabet[i]));
            // Un emoji suelto es medio par sustituto: se completa para que la palabra sea texto válido.
            return w.Replace("\uD83D", "", StringComparison.Ordinal).Replace("\uDE00", "😀", StringComparison.Ordinal) is { Length: > 0 } ok ? ok : "x";
        }));

    /// <summary>Un subtítulo: palabras con sus huecos y largos (en cuadros, a veces con ticks sueltos) y cómo se separan en el texto.</summary>
    internal sealed record SubtitleSpec(string[] Words, int[] Gaps, int[] Lengths, int Breaks, int Lead, int Tail, int OffFrame)
    {
        public override string ToString() => Inv($"[{string.Join(" ", Words)}] huecos [{string.Join(",", Gaps)}] largos [{string.Join(",", Lengths)}] saltos {Breaks} antes {Lead} después {Tail} ticks {OffFrame}");
    }

    private static readonly Gen<SubtitleSpec> GenSubtitle =
        Gen.Int[1, 14].SelectMany(n => Gen.Select(
            GenWord.Array[n], Gen.Int[0, 6].Array[n], Gen.Int[0, 15].Array[n], Gen.Int[0, 65535], Gen.Int[-3, 5], Gen.Int[0, 8], Gen.Int[0, 3],
            (w, g, l, b, lead, tail, off) => new SubtitleSpec(w, g, l, b, lead, tail, off)));

    private static readonly Gen<(SubtitleSpec Left, SubtitleSpec Right, int Gap, int Start)> GenPair =
        Gen.Select(GenSubtitle, GenSubtitle, Gen.Int[0, 30], Gen.Int[0, 300]);

    private static readonly Rational Rate = Rational.Fps30;

    private static Time F(long frames) => Time.FromFrames(frames, Rate);

    /// <summary>Un clip de subtítulo con sus palabras; su origen empieza en 0 en la primera palabra menos <c>Lead</c> cuadros.</summary>
    private static Clip Subtitle(string id, SubtitleSpec spec, Time start)
    {
        var words = new List<GeneratorWord>();
        Time at = Time.Zero;
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < spec.Words.Length; i++)
        {
            at += F(spec.Gaps[i]);
            Time end = at + F(spec.Lengths[i]) + Time.FromTicks(spec.OffFrame * 1_234L);
            words.Add(new GeneratorWord { Start = at, End = end, Text = spec.Words[i] });
            if (i > 0)
            {
                text.Append(((spec.Breaks >> (i % 16)) & 3) == 0 ? '\n' : ' ');
            }

            text.Append(spec.Words[i]);
            at = end;
        }

        Time sourceIn = F(-spec.Lead);
        Time sourceOut = Time.Max(at, sourceIn) + F(1 + spec.Tail);
        return new Clip
        {
            Id = id,
            Start = start,
            SourceIn = sourceIn,
            SourceOut = sourceOut,
            Generator = new ClipGenerator { Text = text.ToString(), Style = new TextStyle(), Words = words },
        };
    }

    private static Project Project(params Clip[] clips) => new()
    {
        Id = "p",
        Name = "Subtítulos",
        Sequences =
        [
            new Sequence
            {
                Id = Seq,
                FrameRate = Rate,
                VideoTracks = [new Track { Id = "v1", Kind = TrackKind.Video, Name = "Subtítulos", Role = TrackRoles.Subtitles, Clips = [.. clips] }],
                AudioTracks = [new Track { Id = "a1", Kind = TrackKind.Audio, Name = "Audio 1" }],
            },
        ],
    };

    /// <summary>Los trozos del texto entre espacios y saltos: lo que se lee, sin el reparto en líneas.</summary>
    private static List<string> Tokens(string text) => [.. text.Split((char[])[' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)];

    private static List<string> WordTexts(Clip clip) => [.. clip.Generator!.Words!.Select(w => w.Text)];

    /// <summary>Dónde cae cada palabra en la línea de tiempo: su inicio y su fin en tiempo de la secuencia.</summary>
    private static List<(long, long, string)> Placed(Clip clip) =>
        [.. clip.Generator!.Words!.Select(w => ((clip.Start + (w.Start - clip.SourceIn)).Ticks, (clip.Start + (w.End - clip.SourceIn)).Ticks, w.Text))];

    /// <summary>
    /// Dividir en cualquier palabra reparte palabras y texto entre los dos trozos sin perder ni repetir nada, deja cada
    /// palabra en su lugar de la línea de tiempo y, al unir los dos trozos, vuelve el mismo clip con las mismas palabras.
    /// </summary>
    [Fact]
    public void DividirYUnir_ConservanPalabrasYTexto()
    {
        int splits = 0, total = 0;
        Gen.Select(GenSubtitle, Gen.Int[0, 300], Gen.Int[1, 13]).Sample(
            s =>
            {
                total++;
                if (s.Item1.Words.Length < 2)
                {
                    return;
                }

                Clip clip = Subtitle("s1", s.Item1, F(s.Item2));
                Project project = Project(clip);
                Assert.Empty(ModelValidator.Validate(project));
                int index = 1 + (s.Item3 % (s.Item1.Words.Length - 1));
                string before = ProjectSerializer.Serialize(project);
                List<string> tokens = Tokens(clip.Generator!.Text);
                List<string> texts = WordTexts(clip);
                List<(long, long, string)> placed = Placed(clip);
                (Time start, Time end, Time sourceIn) = (clip.Start, clip.End, clip.SourceIn);

                var stack = new UndoStack(project);
                SequenceEditCommand split = Edits.SplitSubtitleAtWord(Seq, "s1", index, new SequentialIdGenerator());
                try
                {
                    stack.Execute(split);
                }
                catch (EditException)
                {
                    Assert.Equal(before, ProjectSerializer.Serialize(project));
                    return;
                }

                splits++;
                Assert.Empty(ModelValidator.Validate(project));
                Sequence sequence = project.GetSequence(Seq);
                Clip left = sequence.GetClip("s1").Clip;
                Clip right = sequence.GetClip(split.CreatedIds[0]).Clip;
                Assert.Equal(texts, [.. WordTexts(left), .. WordTexts(right)]);
                Assert.Equal(tokens, [.. Tokens(left.Generator!.Text), .. Tokens(right.Generator!.Text)]);
                Assert.Equal(texts[..index], WordTexts(left));
                Assert.Equal(placed, [.. Placed(left), .. Placed(right)]);
                Assert.Equal((start, end), (left.Start, right.End));
                Assert.Equal(left.End, right.Start);

                stack.Execute(Edits.JoinSubtitleWithNext(Seq, "s1"));
                Assert.Empty(ModelValidator.Validate(project));
                Clip joined = Assert.Single(sequence.VideoTracks[0].Clips);
                Assert.Equal((start, end, sourceIn), (joined.Start, joined.End, joined.SourceIn));
                Assert.Equal(texts, WordTexts(joined));
                Assert.Equal(tokens, Tokens(joined.Generator!.Text));
                Assert.Equal(placed, Placed(joined));

                stack.Undo();
                stack.Undo();
                Assert.Equal(before, ProjectSerializer.Serialize(project));
            },
            writeLine: output.WriteLine,
            iter: 5000,
            print: s => Inv($"{s.Item1} en {s.Item2}, palabra {s.Item3}"));
        output.WriteLine($"{splits} de {total} divisiones");
        Assert.True(splits * 2 >= total, $"Pocas divisiones: {splits} de {total}");
    }

    /// <summary>
    /// Unir dos subtítulos seguidos (pegados o con hueco) deja todas las palabras en su lugar de la línea de tiempo y el texto
    /// de los dos, en orden; el clip va del inicio del primero al final del segundo.
    /// </summary>
    [Fact]
    public void Unir_ConservaElLugarDeCadaPalabra() =>
        GenPair.Sample(
            s =>
            {
                Clip left = Subtitle("s1", s.Left, F(s.Start));
                Clip right = Subtitle("s2", s.Right, left.End + F(s.Gap));
                Project project = Project(left, right);
                Assert.Empty(ModelValidator.Validate(project));
                List<(long, long, string)> placed = [.. Placed(left), .. Placed(right)];
                List<string> tokens = [.. Tokens(left.Generator!.Text), .. Tokens(right.Generator!.Text)];
                Time end = right.End;
                string before = ProjectSerializer.Serialize(project);

                var stack = new UndoStack(project);
                stack.Execute(Edits.JoinSubtitleWithNext(Seq, "s1"));
                Assert.Empty(ModelValidator.Validate(project));
                Clip joined = Assert.Single(project.GetSequence(Seq).VideoTracks[0].Clips);
                Assert.Equal((F(s.Start), end), (joined.Start, joined.End));
                Assert.Equal(placed, Placed(joined));
                Assert.Equal(tokens, Tokens(joined.Generator!.Text));
                stack.Undo();
                Assert.Equal(before, ProjectSerializer.Serialize(project));
            },
            writeLine: output.WriteLine,
            iter: 5000,
            print: s => Inv($"{s.Left} + {s.Right}, hueco {s.Gap}, en {s.Start}"));

    /// <summary>
    /// Corregir una palabra cambia solo esa: en las palabras y en el texto queda en su lugar lo escrito (partido en sus
    /// espacios), las demás no se mueven y las nuevas se reparten sin huecos el tiempo de la vieja.
    /// </summary>
    [Fact]
    public void Corregir_CambiaSoloEsaPalabra()
    {
        int corrected = 0, total = 0;
        Gen.Select(GenSubtitle, Gen.Int[0, 13], GenWord.Array[0, 3], Gen.Int[0, 3]).Sample(
            s =>
            {
                total++;
                Clip clip = Subtitle("s1", s.Item1, F(10));
                Project project = Project(clip);
                int index = s.Item2 % s.Item1.Words.Length;
                string spacing = s.Item4 switch { 0 => " ", 1 => "  ", 2 => "\t", _ => " \n " };
                string replacement = (s.Item4 == 3 ? " " : "") + string.Join(spacing, s.Item3);
                List<GeneratorWord> old = [.. clip.Generator!.Words!.Select(w => w.Clone())];
                List<string> tokens = Tokens(clip.Generator.Text);
                string before = ProjectSerializer.Serialize(project);

                var stack = new UndoStack(project);
                try
                {
                    stack.Execute(Edits.CorrectSubtitleWord(Seq, "s1", index, replacement));
                }
                catch (EditException)
                {
                    Assert.True(s.Item3.Length == 0 && old.Count == 1, "Rechazó una corrección válida");
                    Assert.Equal(before, ProjectSerializer.Serialize(project));
                    return;
                }

                corrected++;
                Assert.Empty(ModelValidator.Validate(project));
                ClipGenerator g = project.GetSequence(Seq).GetClip("s1").Clip.Generator!;
                List<GeneratorWord> now = g.Words!;
                List<string> expected = [.. old.Take(index).Select(w => w.Text), .. s.Item3, .. old.Skip(index + 1).Select(w => w.Text)];
                Assert.Equal(expected, now.Select(w => w.Text));
                Assert.Equal([.. tokens.Take(index), .. s.Item3, .. tokens.Skip(index + 1)], Tokens(g.Text));
                for (int i = 0; i < index; i++)
                {
                    Assert.True(old[i].ContentEquals(now[i]));
                }

                for (int i = index + 1; i < old.Count; i++)
                {
                    Assert.True(old[i].ContentEquals(now[i - 1 + s.Item3.Length]));
                }

                List<GeneratorWord> added = now.GetRange(index, s.Item3.Length);
                if (added.Count > 0)
                {
                    Assert.Equal(old[index].Start, added[0].Start);
                    Assert.Equal(old[index].End, added[^1].End);
                    for (int i = 1; i < added.Count; i++)
                    {
                        Assert.Equal(added[i - 1].End, added[i].Start);
                    }
                }

                stack.Undo();
                Assert.Equal(before, ProjectSerializer.Serialize(project));
            },
            writeLine: output.WriteLine,
            iter: 5000,
            print: s => Inv($"{s.Item1}, palabra {s.Item2} -> [{string.Join("|", s.Item3)}] separadas con {s.Item4}"));
        Assert.True(corrected * 2 >= total, $"Pocas correcciones: {corrected} de {total}");
    }

    /// <summary>
    /// Con el texto editado a mano (palabras de más o cambiadas), cada palabra que se encuentra apunta a un trozo igual del
    /// texto (sin distinguir mayúsculas), en orden y sin repetir trozos.
    /// </summary>
    [Fact]
    public void Rangos_ApuntanATrozosIgualesEnOrden() =>
        Gen.Select(GenWord.Array[1, 12], GenWord.Array[0, 12], Gen.Int[0, 65535]).Sample(
            s =>
            {
                string[] words = s.Item1;
                // El texto: las palabras con algunas cambiadas a mayúsculas, quitadas o con otras metidas entre medio.
                var parts = new List<string>();
                int bits = s.Item3;
                for (int i = 0; i < words.Length; i++)
                {
                    int b = (bits >> (i % 15)) & 3;
                    if (b == 1 && i < s.Item2.Length)
                    {
                        parts.Add(s.Item2[i]);
                    }

                    if (b != 2)
                    {
                        parts.Add(b == 3 ? words[i].ToUpperInvariant() : words[i]);
                    }
                }

                string text = string.Join(" ", parts);
                List<GeneratorWord> list = [.. words.Select((w, i) => new GeneratorWord { Start = F(i), End = F(i + 1), Text = w })];
                IReadOnlyList<WordRange> ranges = SubtitleEdits.WordRanges(text, list);
                Assert.Equal(words.Length, ranges.Count);
                bool positional = parts.Count == words.Length;
                int last = -1;
                for (int i = 0; i < ranges.Count; i++)
                {
                    if (!ranges[i].Found)
                    {
                        Assert.False(positional);
                        continue;
                    }

                    Assert.True(ranges[i].Start > last);
                    last = ranges[i].Start + ranges[i].Length - 1;
                    string piece = text.Substring(ranges[i].Start, ranges[i].Length);
                    Assert.DoesNotContain(' ', piece);
                    if (!positional)
                    {
                        Assert.Equal(words[i], piece, ignoreCase: true);
                    }
                }

                if (parts.SequenceEqual(words))
                {
                    Assert.All(ranges, r => Assert.True(r.Found));
                }
            },
            writeLine: output.WriteLine,
            iter: 20000,
            print: s => Inv($"[{string.Join(" ", s.Item1)}] / [{string.Join(" ", s.Item2)}] / {s.Item3}"));
}
