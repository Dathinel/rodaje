using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Editing;

public class CompoundTests
{
    private const string Seq = "seq-1";

    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static Clip C(string id, double start, double sourceIn, double length, string? link = null) =>
        new() { Id = id, MediaId = "m", Start = S(start), SourceIn = S(sourceIn), SourceOut = S(sourceIn + length), LinkId = link };

    /// <summary>V1: a [0,4) b [4,10) con transición, c [12,14); V2: t (título) [2,6); A1: a' [0,4) enlazado con a.</summary>
    private static Project Fixture()
    {
        var title = new Clip
        {
            Id = "t",
            Start = S(2),
            SourceIn = S(0),
            SourceOut = S(4),
            Generator = new ClipGenerator { Text = "Hola", Style = new TextStyle() },
        };
        return new Project
        {
            Id = "p",
            Media = [new MediaRef { Id = "m", Path = "m.mp4", Duration = S(60), HasVideo = true, HasAudio = true, FrameRate = Rational.Fps30, Width = 1920, Height = 1080, SampleRate = 48000, AudioChannels = 2 }],
            Sequences =
            [
                new Sequence
                {
                    Id = Seq,
                    VideoTracks =
                    [
                        new Track
                        {
                            Id = "v1", Kind = TrackKind.Video, Name = "Video 1",
                            Clips = [C("a", 0, 0, 4, "l1"), C("b", 4, 10, 6), C("c", 12, 30, 2)],
                            Transitions = [new TransitionInstance { Id = "x", Type = "rodaje.crossfade", From = "a", To = "b", Duration = S(1) }],
                        },
                        new Track { Id = "v2", Kind = TrackKind.Video, Name = "Video 2", Clips = [title] },
                    ],
                    AudioTracks = [new Track { Id = "a1", Kind = TrackKind.Audio, Name = "Audio 1", Clips = [C("a2", 0, 0, 4, "l1")] }],
                },
            ],
        };
    }

    [Fact]
    public void Create_moves_the_clips_into_a_new_sequence_and_links_the_two_compounds()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        SequenceEditCommand create = Edits.CreateCompound(Seq, ["a", "b", "t"], "Compuesto 1", new SequentialIdGenerator());
        stack.Execute(create);

        Sequence outer = project.GetSequence(Seq);
        Sequence nested = project.GetSequence(create.CreatedIds[^1]);
        Clip video = outer.VideoTracks[0].Clips[0];
        Clip audio = outer.AudioTracks[0].Clips[0];
        Assert.Equal(nested.Id, video.SequenceId);
        Assert.Equal(nested.Id, audio.SequenceId);
        Assert.Equal((S(0), S(10), S(0)), (video.SourceIn, video.SourceOut, video.Start));
        Assert.NotNull(video.LinkId);
        Assert.Equal(video.LinkId, audio.LinkId);
        Assert.Equal(["c"], outer.VideoTracks[0].Clips.Skip(1).Select(c => c.Id));
        Assert.Empty(outer.VideoTracks[1].Clips);
        Assert.Equal(2, nested.VideoTracks.Count);
        Assert.Equal(["a", "b"], nested.VideoTracks[0].Clips.Select(c => c.Id));
        Assert.Equal("x", Assert.Single(nested.VideoTracks[0].Transitions).Id);
        Assert.Empty(ModelValidator.Validate(project));
    }

    [Fact]
    public void Create_then_decompose_is_the_identity_and_each_is_one_undo_step()
    {
        Project project = Fixture();
        string original = ProjectSerializer.Serialize(project);
        var stack = new UndoStack(project);
        var ids = new SequentialIdGenerator();
        SequenceEditCommand create = Edits.CreateCompound(Seq, ["b", "t", "a"], "Compuesto", ids);
        stack.Execute(create);
        string created = ProjectSerializer.Serialize(project);
        stack.Execute(Edits.DecomposeCompound(Seq, create.CreatedIds[0], ids));

        Assert.Equal(original, ProjectSerializer.Serialize(project));
        stack.Undo();
        Assert.Equal(created, ProjectSerializer.Serialize(project));
        stack.Undo();
        Assert.Equal(original, ProjectSerializer.Serialize(project));
        stack.Redo();
        Assert.Equal(created, ProjectSerializer.Serialize(project));
        stack.Redo();
        Assert.Equal(original, ProjectSerializer.Serialize(project));
    }

    [Fact]
    public void Create_rejects_a_host_track_with_an_unselected_clip_in_the_range_and_leaves_the_project_intact()
    {
        Project project = Fixture();
        string original = ProjectSerializer.Serialize(project);
        var stack = new UndoStack(project);

        Assert.Throws<EditException>(() => stack.Execute(Edits.CreateCompound(Seq, ["a", "c"], "X")));

        Assert.Equal(original, ProjectSerializer.Serialize(project));
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void Decompose_of_a_shared_sequence_copies_with_new_ids_and_keeps_the_sequence()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        var ids = new SequentialIdGenerator();
        SequenceEditCommand create = Edits.CreateCompound(Seq, ["c"], "C", ids);
        stack.Execute(create);
        string nestedId = create.CreatedIds[^1];
        Sequence outer = project.GetSequence(Seq);
        outer.VideoTracks[1].Clips.Add(new Clip { Id = "copia", SequenceId = nestedId, Start = S(20), SourceIn = S(0), SourceOut = S(2) });

        stack.Execute(Edits.DecomposeCompound(Seq, create.CreatedIds[0], ids));

        Assert.NotNull(project.FindSequence(nestedId));
        Clip restored = outer.VideoTracks[0].Clips.Single(c => c.Start == S(12));
        Assert.NotEqual("c", restored.Id);
        Assert.Equal((S(30), S(32)), (restored.SourceIn, restored.SourceOut));
        Assert.Empty(ModelValidator.Validate(project));
    }

    [Fact]
    public void A_trimmed_compound_flattens_to_its_window_and_a_title_keeps_its_animation_offsets()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        SequenceEditCommand create = Edits.CreateCompound(Seq, ["a", "b", "t"], "Compuesto", new SequentialIdGenerator());
        stack.Execute(create);
        stack.Execute(Edits.Trim(Seq, create.CreatedIds[0], TrimEdge.In, S(3)));

        Sequence flat = CompoundOps.Flatten(project, project.GetSequence(Seq));

        Assert.Equal(3, flat.VideoTracks.Count);
        Track lane0 = flat.VideoTracks[0];
        string p = create.CreatedIds[0] + "/";
        Assert.Equal([p + "a", p + "b", "c"], lane0.Clips.Select(c => c.Id));
        Clip a = lane0.Clips[0];
        Assert.Equal((S(3), S(3), S(4)), (a.Start, a.SourceIn, a.SourceOut));
        Assert.Equal(p + "x", Assert.Single(lane0.Transitions).Id);
        Clip title = Assert.Single(flat.VideoTracks[1].Clips);
        Assert.Equal((S(3), S(1)), (title.Start, title.SourceIn));
        Assert.Equal(S(1), title.Generator!.Edges.Before);
        Assert.Equal(S(3), flat.AudioTracks[0].Clips[0].Start);
        Assert.Empty(ModelValidator.ValidateSequence(project, flat));
    }

    [Fact]
    public void Nested_compounds_flatten_recursively()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        var ids = new SequentialIdGenerator();
        SequenceEditCommand inner = Edits.CreateCompound(Seq, ["a"], "Dentro", ids);
        stack.Execute(inner);
        SequenceEditCommand outer = Edits.CreateCompound(Seq, [inner.CreatedIds[0], "b"], "Fuera", ids);
        stack.Execute(outer);

        Sequence flat = CompoundOps.Flatten(project, project.GetSequence(Seq));

        Clip a = flat.VideoTracks[0].Clips[0];
        Assert.Equal($"{outer.CreatedIds[0]}/{inner.CreatedIds[0]}/a", a.Id);
        Assert.Equal((S(0), S(0), S(4)), (a.Start, a.SourceIn, a.SourceOut));
        Assert.Equal(S(4), flat.VideoTracks[0].Clips[1].Start);
    }

    [Fact]
    public void Cycles_are_rejected_by_the_validator_and_ignored_when_flattening()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        SequenceEditCommand create = Edits.CreateCompound(Seq, ["c"], "C", new SequentialIdGenerator());
        stack.Execute(create);
        Sequence nested = project.GetSequence(create.CreatedIds[^1]);
        nested.VideoTracks[0].Clips.Add(new Clip { Id = "vuelta", SequenceId = Seq, Start = S(5), SourceIn = S(0), SourceOut = S(1) });

        Assert.Contains(ModelValidator.Validate(project), e => e.Contains("se contiene a sí mismo", StringComparison.Ordinal));
        Sequence flat = CompoundOps.Flatten(project, project.GetSequence(Seq));
        Assert.DoesNotContain(flat.AllTracks.SelectMany(t => t.Clips), c => c.Id.EndsWith("vuelta", StringComparison.Ordinal));
    }

    [Fact]
    public void A_compound_does_not_accept_speed_or_media_edits()
    {
        Project project = Fixture();
        var stack = new UndoStack(project);
        SequenceEditCommand create = Edits.CreateCompound(Seq, ["c"], "C", new SequentialIdGenerator());
        stack.Execute(create);

        EditException speed = Assert.Throws<EditException>(() => stack.Execute(Edits.SetSpeed(Seq, create.CreatedIds[0], new Rational(2, 1))));
        Assert.Contains("compuesto", speed.Message, StringComparison.Ordinal);
        Assert.Throws<EditException>(() => stack.Execute(Edits.Trim(Seq, create.CreatedIds[0], TrimEdge.Out, S(1))));
    }

    [Fact]
    public void Format_6_round_trips_a_compound()
    {
        Project project = Fixture();
        new UndoStack(project).Execute(Edits.CreateCompound(Seq, ["a", "b"], "C", new SequentialIdGenerator()));

        string json = ProjectSerializer.Serialize(project);
        Project read = ProjectSerializer.Deserialize(json);

        Assert.Contains("\"sequenceId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 9", json, StringComparison.Ordinal);
        Assert.Equal(json, ProjectSerializer.Serialize(read));
    }

    /// <summary>Selecciones al azar sobre proyectos al azar: crear y descomponer deja el archivo igual, y un rechazo no toca nada.</summary>
    [Fact]
    public void Property_create_then_decompose_is_identity()
    {
        int created = 0;
        for (int seed = 0; seed < 400; seed++)
        {
            var random = new Random(seed);
            Project project = RandomProject(random);
            string original = ProjectSerializer.Serialize(project);
            var stack = new UndoStack(project);
            var ids = new SequentialIdGenerator();
            List<string> all = [.. project.Sequences[0].AllTracks.SelectMany(t => t.Clips).Select(c => c.Id)];
            List<string> chosen = [.. all.Where(_ => random.Next(3) == 0)];
            if (chosen.Count == 0)
            {
                continue;
            }

            SequenceEditCommand create = Edits.CreateCompound(Seq, chosen, "R", ids);
            try
            {
                stack.Execute(create);
            }
            catch (EditException)
            {
                Assert.Equal(original, ProjectSerializer.Serialize(project));
                continue;
            }

            created++;
            Assert.Empty(ModelValidator.Validate(project));
            stack.Execute(Edits.DecomposeCompound(Seq, create.CreatedIds[0], ids));
            Assert.Equal(original, ProjectSerializer.Serialize(project));
            stack.Undo();
            stack.Undo();
            Assert.Equal(original, ProjectSerializer.Serialize(project));
        }

        Assert.True(created > 100, $"Solo {created} casos llegaron a crear un compuesto.");
    }

    private static Project RandomProject(Random random)
    {
        Project project = Fixture();
        Sequence sequence = project.Sequences[0];
        foreach (Track track in sequence.AllTracks)
        {
            track.Clips.Clear();
            track.Transitions.Clear();
        }

        int n = 0;
        foreach (Track track in sequence.AllTracks)
        {
            double at = random.Next(0, 3);
            for (int i = random.Next(0, 4); i > 0; i--)
            {
                double length = random.Next(1, 5);
                track.Clips.Add(C($"r{n++}", at, random.Next(0, 40), length));
                at += length + (random.Next(2) == 0 ? 0 : random.Next(1, 3));
            }
        }

        // Algunos pares video/audio enlazados en el mismo lugar.
        foreach (Clip video in sequence.VideoTracks[0].Clips.Where(_ => random.Next(2) == 0))
        {
            Track audio = sequence.AudioTracks[0];
            if (TrackOps.FindOverlap(audio, video.Start, video.End) is null)
            {
                video.LinkId = $"l{n}";
                TrackOps.AddClipSorted(audio, new Clip { Id = $"r{n++}", MediaId = "m", Start = video.Start, SourceIn = video.SourceIn, SourceOut = video.SourceOut, LinkId = video.LinkId });
            }
        }

        Assert.Empty(ModelValidator.Validate(project));
        return project;
    }
}
