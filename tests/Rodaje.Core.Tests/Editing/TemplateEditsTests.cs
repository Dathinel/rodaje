using System.Text.Json;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Model.ModelSamples;

namespace Rodaje.Core.Tests.Editing;

/// <summary>Clips de plantilla de movimiento (D-064) con las ediciones de siempre y las suyas.</summary>
public sealed class TemplateEditsTests
{
    private const string Seq = "s1";

    private static (Project Project, string ClipId) WithTemplate()
    {
        Project project = ValidProject();
        Clip clip = MakeTemplate("t1", 0, 5);
        SequenceEditCommand insert = Edits.InsertGenerator(Seq, "v2", Sec(40), Sec(5), clip.Generator!, new SequentialIds());
        insert.Apply(project);
        return (project, insert.CreatedIds[0]);
    }

    private static Clip Find(Project project, string id) => project.Sequences[0].FindClip(id)!.Value.Clip;

    [Fact]
    public void Insert_save_and_open_give_the_same_project()
    {
        (Project project, string id) = WithTemplate();

        string saved = ProjectSerializer.Serialize(project);
        Project read = ProjectSerializer.Deserialize(saved);

        Assert.Equal(GeneratorKinds.Template, Find(read, id).Generator!.Kind);
        Assert.Equal(saved, ProjectSerializer.Serialize(read));
    }

    [Fact]
    public void Changing_values_is_one_step_and_undo_gives_back_the_same_bytes()
    {
        (Project project, string id) = WithTemplate();
        string before = ProjectSerializer.Serialize(project);
        List<TemplateLayer> layers = Find(project, id).Generator!.Template!.Layers.ConvertAll(l => l.Clone());
        layers[0].Text = "Luis Gómez";

        SequenceEditCommand change = Edits.SetTemplateValues(Seq, id,
            new Dictionary<string, JsonElement> { ["nombre"] = JsonDocument.Parse("\"Luis Gómez\"").RootElement }, layers);
        change.Apply(project);
        Assert.Equal("Luis Gómez", Find(project, id).Generator!.Template!.Layers[0].Text);

        change.Revert(project);
        Assert.Equal(before, ProjectSerializer.Serialize(project));
    }

    [Fact]
    public void Trimming_stops_at_the_template_minimum()
    {
        (Project project, string id) = WithTemplate();

        Assert.Throws<EditException>(() => Edits.Trim(Seq, id, TrimEdge.Out, Sec(-3.6)).Apply(project));
        Edits.Trim(Seq, id, TrimEdge.Out, Sec(-3.5)).Apply(project);

        Assert.Equal(Sec(1.5), Find(project, id).Duration);
        Assert.Equal(Sec(1.5), TemplateEdits.MinDuration(Find(project, id), Time.FromSeconds(1.0 / 30)));
    }

    [Fact]
    public void Copies_and_duplicates_do_not_share_layers()
    {
        (Project project, string id) = WithTemplate();
        SequenceEditCommand duplicate = Edits.DuplicateClips(Seq, [id], new SequentialIds("d"));
        duplicate.Apply(project);
        ClipClipboard copied = ClipClipboard.Capture(project.Sequences[0], [id])!;
        Project other = ValidProject();
        SequenceEditCommand paste = Edits.PasteClips(Seq, copied, Sec(100), new SequentialIds("p"));
        paste.Apply(other);

        foreach (Clip copy in new[] { Find(project, duplicate.CreatedIds[0]), Find(other, paste.CreatedIds[0]) })
        {
            Assert.True(copy.Generator!.ContentEquals(Find(project, id).Generator!));
            copy.Generator!.Template!.Layers[0].Text = "Cambiada";
            copy.Generator.Template.Layers[1].Style.Size = 12;
        }

        Assert.Equal("Ana Ruiz", Find(project, id).Generator!.Template!.Layers[0].Text);
        Assert.Equal(96, Find(project, id).Generator!.Template!.Layers[1].Style.Size);
    }

    [Fact]
    public void Splitting_keeps_the_template_and_records_the_edges()
    {
        (Project project, string id) = WithTemplate();
        SequenceEditCommand split = Edits.Split(Seq, id, Sec(40.1), new SequentialIds("x"));
        split.Apply(project);

        Clip left = Find(project, id);
        Clip right = Find(project, split.CreatedIds[0]);
        Assert.True(left.Generator!.SameContent(right.Generator!));
        Assert.Equal(Sec(0.1), right.Generator!.Edges.Before);
        Assert.Empty(ModelValidator.Validate(project));
    }

    [Fact]
    public void Converting_a_piece_where_no_layer_shows_fails_and_keeps_the_template()
    {
        (Project project, string id) = WithTemplate();
        Find(project, id).Generator!.Template!.Layers.ForEach(l => l.Start = 10);

        Assert.Throws<EditException>(() => Edits.ConvertTemplateToTitles(Seq, id, new SequentialIds("c")).Apply(project));
        Assert.NotNull(project.Sequences[0].FindClip(id));
    }

    [Fact]
    public void Converting_to_titles_puts_one_title_per_layer_above_and_undo_restores_the_template()
    {
        (Project project, string id) = WithTemplate();
        Clip template = Find(project, id);
        template.Effects.Add(new EffectInstance { Id = "fx-t", Type = BuiltInEffects.Transform, Parameters = { ["y"] = new Parameter { Value = 10 } } });
        string before = ProjectSerializer.Serialize(project);

        SequenceEditCommand convert = Edits.ConvertTemplateToTitles(Seq, id, new SequentialIds("c"));
        convert.Apply(project);

        Clip name = Find(project, convert.CreatedIds[0]);
        Clip role = Find(project, convert.CreatedIds[1]);
        Assert.Null(project.Sequences[0].FindClip(id));
        Assert.Equal(("Ana Ruiz", Sec(40), Sec(45)), (name.Generator!.Text, name.Start, name.End));
        Assert.Equal(("Directora", Sec(40.2), Sec(44.9)), (role.Generator!.Text, role.Start, role.End));
        EffectInstance transform = role.Effects.Single(e => e.Type == BuiltInEffects.Transform);
        Assert.Equal(-560, transform.Parameters["x"].Value, 9);
        Assert.Equal(460, transform.Parameters["y"].Value, 9);
        Assert.True(project.Sequences[0].VideoTracks.IndexOf(project.Sequences[0].FindClip(role.Id)!.Value.Track)
            > project.Sequences[0].VideoTracks.IndexOf(project.Sequences[0].FindClip(name.Id)!.Value.Track));
        Assert.Empty(ModelValidator.Validate(project));

        convert.Revert(project);
        Assert.Equal(before, ProjectSerializer.Serialize(project));
    }

    private sealed class SequentialIds(string prefix = "n") : IIdGenerator
    {
        private int _next;

        public string NewId(string kind) => $"{prefix}-{kind}-{++_next}";
    }
}
