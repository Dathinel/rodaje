using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Xunit.Sdk;

namespace Rodaje.Core.Tests.Properties;

public sealed class SeededEditingInvariantsTests
{
    public static TheoryData<int> Seeds => new(Enumerable.Range(41001, 20));

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Mixed_edits_preserve_invariants_and_the_complete_history(int seed)
    {
        var random = new Random(seed);
        var ids = new SequentialIdGenerator();
        var project = new Project
        {
            Id = "project", Name = "Edición aleatoria ñ",
            Media = [new MediaRef { Id = "media", Name = "Toma", Path = "toma.mp4", HasVideo = true,
                Duration = F(30000), FrameRate = Rational.Fps30, Width = 1920, Height = 1080 }],
            Sequences = [new Sequence { Id = "sequence", Name = "Principal", VideoTracks =
                [new Track { Id = "v1", Kind = TrackKind.Video }, new Track { Id = "v2", Kind = TrackKind.Video },
                    new Track { Id = "v3", Kind = TrackKind.Video }] }],
        };
        var stack = new UndoStack(project, 1000);
        string initial = ProjectSerializer.Serialize(project);
        int step = -1;
        string operation = "inicializar";
        var successes = new int[15];
        try
        {
            // Cada bloque baraja las quince operaciones: ninguna familia desaparece por azar.
            for (int cycle = 0; cycle < 20; cycle++)
            {
                int[] operations = [.. Enumerable.Range(0, 15)];
                random.Shuffle(operations);
                foreach (int kind in operations)
                {
                    step++;
                    Sequence sequence = project.Sequences[0];
                    Clip[] clips = [.. sequence.AllTracks.SelectMany(t => t.Clips)];
                    Clip? clip = clips.Length == 0 ? null : clips[random.Next(clips.Length)];
                    Track track = sequence.VideoTracks[random.Next(3)];
                    string before = ProjectSerializer.Serialize(project);
                    operation = $"{kind}, clip={clip?.Id}, pista={track.Id}";
                    if (kind is 13 or 14)
                    {
                        if (kind == 13 ? stack.Undo() : stack.Redo())
                        {
                            successes[kind]++;
                        }
                    }
                    else
                    {
                        // Al faltar clips, sembrar material es una operación real del historial.
                        ICommand command = clip is null || kind == 0 ? Add() : Command(kind, clip);
                        Check(project);
                        before = ProjectSerializer.Serialize(project);
                        operation += $", {command.Description}";
                        stack.BreakMerge();
                        try
                        {
                            stack.Execute(command);
                            successes[kind]++;
                        }
                        catch (EditException)
                        {
                            Assert.Equal(before, ProjectSerializer.Serialize(project));
                        }
                    }

                    Check(project);

                    ICommand Add() => Edits.AppendMedia("sequence", "media", F(300), F(420 + random.Next(120)), track.Id, ids: ids);
                    ICommand Command(int k, Clip c) => k switch
                    {
                        1 => Edits.Move("sequence", c.Id, F(random.Next(1200)), ids: ids),
                        2 => Edits.Trim("sequence", c.Id, TrimEdge.In, F(random.Next(-15, 16)), ids),
                        3 => Edits.Trim("sequence", c.Id, TrimEdge.Out, F(random.Next(-15, 16)), ids),
                        4 => Edits.Split("sequence", c.Id, c.Start + Time.FromTicks(c.Duration.Ticks / 2), ids),
                        5 => Edits.Lift("sequence", [c.Id], ids),
                        6 => Edits.RippleDelete("sequence", [c.Id], ids: ids),
                        7 => Edits.Move("sequence", c.Id, c.Start, track.Id, ids: ids),
                        8 => Edits.GroupClips("sequence", clips.Take(2).Select(x => x.Id), ids),
                        9 => Edits.SetSpeed("sequence", c.Id, new Rational(random.Next(1, 5), 2), ids: ids),
                        10 => Transition(),
                        11 => Key(c, move: false),
                        12 => Key(c, move: true),
                        _ => Add(),
                    };
                    ICommand Transition()
                    {
                        // Dos piezas con material a ambos lados garantizan un corte elegible.
                        var append = Edits.AppendMedia("sequence", "media", F(300), F(480), track.Id, ids: ids);
                        stack.Execute(append);
                        Check(project);
                        var second = Edits.AppendMedia("sequence", "media", F(600), F(780), track.Id, ids: ids);
                        stack.Execute(second);
                        Check(project);
                        return Edits.AddTransition("sequence", append.CreatedIds[0], BuiltInTransitions.Crossfade, F(12), ids);
                    }
                    ICommand Key(Clip c, bool move)
                    {
                        if (c.Effects.Count == 0)
                        {
                            stack.Execute(Edits.AddEffect("sequence", c.Id, "rodaje.transform", ids: ids));
                            Check(project);
                        }

                        c = project.Sequences[0].GetClip(c.Id).Clip;
                        EffectInstance effect = c.Effects[0];
                        Time at = c.SourceIn + Time.FromTicks(random.NextInt64(c.SourceDuration.Ticks));
                        const string Name = "opacity";
                        if (move && (!effect.Parameters.TryGetValue(Name, out Parameter? existing) || existing.Keyframes.Count == 0))
                        {
                            stack.Execute(Edits.SetKeyframe("sequence", c.Id, effect.Id, Name, c.SourceIn, 0.5, ids: ids));
                            Check(project);
                            c = project.Sequences[0].GetClip(c.Id).Clip;
                            effect = c.Effects[0];
                        }

                        if (move && effect.Parameters.TryGetValue(Name, out Parameter? parameter) && parameter.Keyframes.Count > 0)
                        {
                            return Edits.MoveKeyframe("sequence", c.Id, effect.Id, Name, parameter.Keyframes[0].Time, at, "key-drag", ids);
                        }

                        return Edits.SetKeyframe("sequence", c.Id, effect.Id, Name, at, random.Next(101) / 100.0, ids: ids);
                    }
                }
            }

            // Deshacer/rehacer consecutivos garantizados, incluso si se vació la rama de rehacer durante la mezcla.
            operation = "deshacer y rehacer inmediato";
            while (stack.Redo())
            {
                Check(project);
            }

            string final = ProjectSerializer.Serialize(project);
            Assert.True(stack.Undo());
            Check(project);
            Assert.True(stack.Redo());
            Check(project);
            Assert.Equal(final, ProjectSerializer.Serialize(project));
            operation = "deshacer todo";
            while (stack.Undo())
            {
                Check(project);
            }

            Assert.Equal(initial, ProjectSerializer.Serialize(project));
            operation = "rehacer todo";
            while (stack.Redo())
            {
                Check(project);
            }

            Assert.Equal(final, ProjectSerializer.Serialize(project));
            Assert.All(successes.Take(14), count => Assert.True(count > 0, "Una operación nunca fue aceptada."));
        }
        catch (Exception error)
        {
            throw new XunitException($"Semilla {seed}, paso {step}, operación {operation}: {error}");
        }
    }

    private static void Check(Project project)
    {
        Assert.Empty(ModelValidator.Validate(project));
        Sequence sequence = project.Sequences[0];
        Time maximum = Time.Zero;
        foreach (Track track in sequence.AllTracks)
        {
            Time end = Time.Zero;
            foreach (Clip clip in track.Clips)
            {
                Assert.True(clip.Duration > Time.Zero);
                Assert.True(clip.Start >= end);
                Assert.True(clip.SourceIn >= Time.Zero);
                end = clip.End;
                maximum = Time.Max(maximum, end);
                foreach (Parameter parameter in clip.Effects.SelectMany(e => e.Parameters.Values))
                {
                    // El modelo conserva claves del origen fuera del recorte para recuperar la animación al extenderlo.
                    Assert.Equal(parameter.Keyframes.OrderBy(k => k.Time), parameter.Keyframes);
                    Assert.Equal(parameter.Keyframes.Count, parameter.Keyframes.Select(k => k.Time).Distinct().Count());
                    Assert.All(parameter.Keyframes, key => Assert.InRange(key.Time.Ticks, 0, project.GetMedia(clip.MediaId).Duration.Ticks));
                    Assert.InRange(clip.ToEffectTime(clip.Start).Ticks, clip.SourceIn.Ticks, clip.SourceOut.Ticks);
                    Assert.InRange(clip.ToEffectTime(clip.End).Ticks, clip.SourceIn.Ticks, clip.SourceOut.Ticks);
                }
            }

            foreach (TransitionInstance transition in track.Transitions)
            {
                Clip from = Assert.Single(track.Clips, c => c.Id == transition.From);
                Clip to = Assert.Single(track.Clips, c => c.Id == transition.To);
                Assert.Equal(from.End, to.Start);
                Assert.True(transition.Duration > Time.Zero);
            }
        }

        Assert.Equal(maximum, sequence.Duration);
    }

    private static Time F(long frame) => Time.FromFrames(frame, Rational.Fps30);
}
