using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Editing.Placement;
using Rodaje.Core.Tests.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Properties;

public sealed class CoreInvariantsPropertiesTests
{
    private const int SeedCount = 64;

    [Fact]
    public void RandomEdits_UndoAndRedoRestoreExactProject()
    {
        for (int seed = 1; seed <= SeedCount; seed++)
        {
            Project project = RoundTripProjects.Generate(seed);
            byte[] initial = System.Text.Encoding.UTF8.GetBytes(ProjectSerializer.Serialize(project));
            var stack = new UndoStack(project);
            var random = new Random(seed);
            string sequenceId = project.Sequences[0].Id;

            for (int edit = 0; edit < 8; edit++)
            {
                string name = $"seed-{seed}-edit-{edit}-{random.Next()}";
                stack.Execute(new SequenceEditCommand("Renombrar secuencia", sequenceId, ctx =>
                {
                    ctx.Sequence.Name = name;
                }));
            }

            byte[] final = System.Text.Encoding.UTF8.GetBytes(ProjectSerializer.Serialize(project));
            while (stack.Undo())
            {
            }

            Assert.True(initial.AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(ProjectSerializer.Serialize(project))),
                $"Undo no restauró exactamente el proyecto de la semilla {seed}.");

            while (stack.Redo())
            {
            }

            Assert.True(final.AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(ProjectSerializer.Serialize(project))),
                $"Redo no restauró exactamente el proyecto de la semilla {seed}.");
        }
    }

    [Fact]
    public void RippleDelete_LeavesLinkedClipsSynchronizedAcrossSeeds()
    {
        for (int seed = 1; seed <= SeedCount; seed++)
        {
            var random = new Random(seed);
            var builder = new PlacementBuilder();
            int headLength = random.Next(1, 16);
            int gap = random.Next(0, 6);
            int pairStart = headLength + gap;
            int pairLength = random.Next(1, 30);
            builder.Add("v1", "head", 0, 0, headLength, media: "vid");
            Clip video = builder.Add("v1", "video", pairStart, 0, pairLength, link: "link");
            Clip audio = builder.Add("a1", "audio", pairStart, 0, pairLength, link: "link");
            Time before = Assert.IsType<Time>(LinkSync.OffsetBetween(video, audio));

            var stack = new UndoStack(builder.Project);
            stack.Execute(Edits.RippleDelete(builder.Sequence.Id, ["head"], RippleScope.Track, builder.Ids));

            Assert.Equal(before, LinkSync.OffsetBetween(video, audio));
            Assert.Equal(PlacementBuilder.F(pairStart - headLength), video.Start);
            Assert.Equal(video.Start, audio.Start);
            builder.AssertValid();
        }
    }

    [Fact]
    public void RippleEdits_KeepClipTicksNonNegativeAcrossSeeds()
    {
        for (int seed = 1; seed <= SeedCount; seed++)
        {
            var random = new Random(seed);
            var builder = new PlacementBuilder();
            int headSourceIn = random.Next(0, 10);
            int headLength = random.Next(2, 16);
            int pairStart = headLength + random.Next(0, 6);
            int pairSourceIn = random.Next(0, 20);
            int pairLength = random.Next(1, 30);
            builder.Add("v1", "head", 0, headSourceIn, headSourceIn + headLength, media: "av");
            builder.Add("v1", "video", pairStart, pairSourceIn, pairSourceIn + pairLength, link: "link");
            builder.Add("a1", "audio", pairStart, pairSourceIn, pairSourceIn + pairLength, link: "link");

            var stack = new UndoStack(builder.Project);
            stack.Execute(Edits.RippleTrim(builder.Sequence.Id, "head", TrimEdge.Out,
                Time.FromFrames(-random.Next(1, headLength), Rational.Fps25), RippleScope.Track, builder.Ids));
            AssertClipTicksNonNegative(builder.Project, seed);

            stack.Execute(Edits.RippleDelete(builder.Sequence.Id, ["head"], RippleScope.Track, builder.Ids));
            AssertClipTicksNonNegative(builder.Project, seed);
        }
    }

    [Fact]
    public void EditedProjects_SaveOpenSaveRoundTripsAcrossSeeds()
    {
        for (int seed = 1; seed <= SeedCount; seed++)
        {
            var random = new Random(seed);
            var builder = new PlacementBuilder();
            int headLength = random.Next(1, 16);
            int pairStart = headLength + random.Next(0, 6);
            int pairLength = random.Next(1, 30);
            builder.Add("v1", "head", 0, 0, headLength, media: "vid");
            builder.Add("v1", "video", pairStart, 0, pairLength, link: "link");
            builder.Add("a1", "audio", pairStart, 0, pairLength, link: "link");
            new UndoStack(builder.Project).Execute(
                Edits.RippleDelete(builder.Sequence.Id, ["head"], RippleScope.Track, builder.Ids));

            string saved = ProjectSerializer.Serialize(builder.Project);
            Project reopened = ProjectSerializer.Deserialize(saved);
            string savedAgain = ProjectSerializer.Serialize(reopened);

            SerializationSamples.AssertDeepEqual(builder.Project, reopened);
            Assert.Equal(saved, savedAgain);
        }
    }

    private static void AssertClipTicksNonNegative(Project project, int seed)
    {
        foreach (Clip clip in project.Sequences.SelectMany(s => s.AllTracks).SelectMany(t => t.Clips))
        {
            Assert.True(clip.Start.Ticks >= 0 && clip.SourceIn.Ticks >= 0 && clip.SourceOut.Ticks >= 0,
                $"La semilla {seed} dejó ticks negativos en el clip {clip.Id}.");
        }

        Assert.Empty(ModelValidator.Validate(project));
    }
}
