using System.Text.Json.Nodes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Model.ModelSamples;

namespace Rodaje.Core.Tests.Model;

/// <summary><c>generator.words</c>: palabras transcritas de un subtítulo, opcionales dentro del formato 5.</summary>
public class GeneratorWordsTests
{
    private static List<GeneratorWord> Words(params (double Start, double End, string Text)[] words) =>
        [.. words.Select(w => new GeneratorWord { Start = Sec(w.Start), End = Sec(w.End), Text = w.Text })];

    private static Project WithTitle(out Clip title)
    {
        Project p = ValidProject();
        title = MakeTitle("t1", 10, 0, 2);
        title.Generator!.Text = "Hola a todos";
        title.Generator.Words = Words((0.1, 0.4, "Hola"), (0.4, 0.5, "a"), (0.5, 1.2, "todos"));
        p.Sequences[0].VideoTracks[1].Clips.Add(title);
        return p;
    }

    private static void AssertError(Action<ClipGenerator> breakIt, string expected)
    {
        Project p = WithTitle(out Clip title);
        breakIt(title.Generator!);
        Assert.Contains(ModelValidator.Validate(p), e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void ValidWords_Pass_AndRoundTrip()
    {
        Project p = WithTitle(out _);
        Assert.Empty(ModelValidator.Validate(p));

        string json = ProjectSerializer.Serialize(p);
        JsonArray words = JsonNode.Parse(json)!["sequences"]![0]!["videoTracks"]![1]!["clips"]![1]!["generator"]!["words"]!.AsArray();
        Assert.Equal(3, words.Count);
        Assert.Equal(Sec(0.1).Ticks, words[0]!["start"]!.GetValue<long>());
        Assert.Equal("todos", words[2]!["text"]!.GetValue<string>());

        Project back = ProjectSerializer.Deserialize(json);
        ClipGenerator generator = back.Sequences[0].GetClip("t1").Clip.Generator!;
        Assert.Equal(["Hola", "a", "todos"], generator.Words!.Select(w => w.Text));
        Assert.Equal(Sec(1.2), generator.Words![2].End);
        Assert.Equal(json, ProjectSerializer.Serialize(back));
    }

    [Fact]
    public void TitleWithoutWords_DoesNotWriteTheField()
    {
        Project p = ValidProject();
        p.Sequences[0].VideoTracks[1].Clips.Add(MakeTitle("t1", 10, 0, 2));
        string json = ProjectSerializer.Serialize(p);
        Assert.DoesNotContain("\"words\"", json, StringComparison.Ordinal);
        Assert.Null(ProjectSerializer.Deserialize(json).Sequences[0].GetClip("t1").Clip.Generator!.Words);
    }

    [Fact]
    public void NullWords_ReadAsNoWords()
    {
        Project p = WithTitle(out _);
        JsonNode root = JsonNode.Parse(ProjectSerializer.Serialize(p))!;
        root["sequences"]![0]!["videoTracks"]![1]!["clips"]![1]!["generator"]!["words"] = null;
        Assert.Null(ProjectSerializer.Deserialize(root.ToJsonString()).Sequences[0].GetClip("t1").Clip.Generator!.Words);
    }

    [Fact]
    public void Clone_CopiesWordsDeeply_AndSameContentComparesThem()
    {
        WithTitle(out Clip title);
        ClipGenerator copy = title.Generator!.Clone();
        Assert.True(copy.ContentEquals(title.Generator));
        Assert.NotSame(title.Generator.Words![0], copy.Words![0]);

        copy.Words[1].Text = "o";
        Assert.False(copy.SameContent(title.Generator));
        copy.Words = null;
        Assert.False(copy.SameContent(title.Generator));
        Assert.False(title.Generator.SameContent(copy));
    }

    [Fact]
    public void WordsBeforeTheOrigin_AreAllowed()
    {
        // El borde izquierdo de un subtítulo cae en el cuadro; la palabra pudo empezar un poco antes.
        Project p = WithTitle(out Clip title);
        title.Generator!.Words![0].Start = Sec(-0.01);
        Assert.Empty(ModelValidator.Validate(p));
    }

    [Fact]
    public void BadWords_AreRejected()
    {
        AssertError(g => g.Words![1] = null!, "palabra transcrita vacía (null)");
        AssertError(g => g.Words![1].Text = "", "palabra transcrita vacía, de más de");
        AssertError(g => g.Words![1].Text = "dos palabras", "con espacios o caracteres de control");
        AssertError(g => g.Words![1].Text = "a\u0001", "con espacios o caracteres de control");
        AssertError(g => g.Words![1].Text = new string('a', ModelValidator.MaxWordLength + 1), "de más de");
        AssertError(g => g.Words![1].End = Sec(0.3), "terminan antes de empezar o fuera de orden");
        AssertError(g => g.Words![2].Start = Sec(0.2), "fuera de orden");
        AssertError(g => g.Words![0].Start = Sec(-400_000), "tiempos fuera de rango");
        AssertError(g => g.Words = [.. Enumerable.Range(0, ModelValidator.MaxGeneratorWords + 1).Select(i => new GeneratorWord { Text = "x" })],
            $"más de {ModelValidator.MaxGeneratorWords} palabras");
    }

    [Fact]
    public void WordsCount_TowardTheProjectTextLimit()
    {
        Project p = ValidProject();
        Track track = p.Sequences[0].VideoTracks[1];
        string word = new('a', ModelValidator.MaxWordLength);
        int perClip = ModelValidator.MaxGeneratorWords;
        int clips = (int)(ModelValidator.MaxProjectTextLength / ((long)perClip * word.Length)) + 1;
        for (int i = 0; i < clips; i++)
        {
            Clip title = MakeTitle($"t{i}", 100 + (i * 2), 0, 1);
            title.Generator!.Words = [.. Enumerable.Range(0, perClip).Select(_ => new GeneratorWord { Text = word })];
            track.Clips.Add(title);
        }

        Assert.Contains(ModelValidator.Validate(p), e => e.Contains("Los textos del proyecto suman", StringComparison.Ordinal));
    }
}
