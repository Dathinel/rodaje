using System.Diagnostics;
using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.TimelineLogic;

/// <summary>
/// Lo que copia cada paso vivo de un arrastre en un proyecto de 320 clips con efectos y keyframes: instantáneas de la
/// secuencia para el historial (medidas en bytes asignados en el hilo de la interfaz) y proyectos para el reproductor.
/// </summary>
public sealed class ClipDragCostTests(ITestOutputHelper output)
{
    private const int Steps = 40;

    /// <summary>
    /// Antes, 958 KB por paso: dos instantáneas enteras, la validación y el proyecto entero para el reproductor. Ahora las
    /// instantáneas solo copian los clips que se mueven (el resto se comparte) y, con dos movimientos por cuadro, el
    /// reproductor recibe una copia cada dos pasos. V1 es el caso típico: el clip lleva su audio enlazado en A1, y las dos
    /// pistas son casi toda la secuencia.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void UnPasoVivo_CopiaSoloLosClipsQueCambianYUnProyectoPorCuadro(int track)
    {
        using var directory = new TempDirectory();
        string path = SaveProject(directory.Path);
        UiThread.Run(async () =>
        {
            var playback = new FakePlayback();
            var frames = new List<Action>();
            var session = new EditorSession(playback, new FakeProber(), new FakeDialogs(), new SequentialIdGenerator()) { NextFrame = frames.Add };
            Assert.True(await session.OpenProjectAsync(path));
            Assert.Equal(320, session.Sequence.AllTracks.Sum(t => t.Clips.Count));

            long snapshot = Allocated(() => SequenceSnapshot.Capture(session.Sequence));
            long clone = Allocated(() => session.Project.Clone());
            long validation = Allocated(() => ModelValidator.ValidateSequence(session.Project, session.Sequence));

            Clip moved = session.Sequence.VideoTracks[track].Clips[^1];
            Clip? linked = session.Sequence.AudioTracks[0].Clips.Find(c => moved.LinkId is not null && c.LinkId == moved.LinkId);
            Assert.Equal(track == 0, linked is not null);
            Time grab = moved.Start;
            var drag = new ClipDragSession(session, new ClipDrag(session.Project, session.SequenceId, moved.Id, DragOperation.Move, grab, new DragOptions(), session.Position));
            drag.Begin();
            drag.Move(grab + Time.FromSeconds(0.05), null);
            NextFrame(frames);
            int reloads = playback.Reloads.Count;
            long start = GC.GetAllocatedBytesForCurrentThread();
            long time = Stopwatch.GetTimestamp();
            for (int i = 1; i <= Steps; i++)
            {
                drag.Move(grab + Time.FromSeconds(i * 0.1), null);
                if (i % 2 == 0)
                {
                    // Dos movimientos por cuadro: un ratón de 120 Hz en una pantalla de 60 Hz.
                    NextFrame(frames);
                }
            }

            double ms = Stopwatch.GetElapsedTime(time).TotalMilliseconds / Steps;
            double perStep = (GC.GetAllocatedBytesForCurrentThread() - start) / (double)Steps;
            double reloadsPerStep = (playback.Reloads.Count - reloads) / (double)Steps;
            double snapshotsPerStep = (perStep - validation - (reloadsPerStep * clone)) / snapshot;
            drag.Complete();

            output.WriteLine($"instantánea: {snapshot / 1024.0:0} KB; Project.Clone: {clone / 1024.0:0} KB; validación: {validation / 1024.0:0} KB");
            output.WriteLine($"V{track + 1}, por paso: {perStep / 1024.0:0} KB asignados, {snapshotsPerStep:0.00} instantáneas, {reloadsPerStep:0.00} proyectos al reproductor, {ms:0.00} ms");
            Time end = grab + Time.FromSeconds(Steps * 0.1);
            Assert.Equal(Steps + 1, drag.LiveSteps);
            Assert.Equal(end, session.Sequence.GetClip(moved.Id).Clip.Start);
            if (linked is not null)
            {
                Assert.Equal(end, session.Sequence.GetClip(linked.Id).Clip.Start);
            }
            Assert.Equal(end, playback.Reloads[^1].GetSequence(session.SequenceId).GetClip(moved.Id).Clip.Start);
            Assert.Equal(1, session.History.Count);
            Assert.True(snapshotsPerStep < 0.25, $"{snapshotsPerStep:0.00} instantáneas por paso");
            Assert.InRange(reloadsPerStep, 0.45, 0.55);
        });
    }

    /// <summary>Al soltar, el reproductor recibe lo último aunque el cuadro todavía no haya llegado.</summary>
    [Fact]
    public void AlSoltar_ElReproductorTieneElUltimoPasoSinEsperarAlCuadro()
    {
        using var directory = new TempDirectory();
        string path = SaveProject(directory.Path);
        UiThread.Run(async () =>
        {
            var playback = new FakePlayback();
            var frames = new List<Action>();
            var session = new EditorSession(playback, new FakeProber(), new FakeDialogs(), new SequentialIdGenerator()) { NextFrame = frames.Add };
            Assert.True(await session.OpenProjectAsync(path));
            Clip moved = session.Sequence.VideoTracks[1].Clips[^1];
            Time grab = moved.Start;
            var drag = new ClipDragSession(session, new ClipDrag(session.Project, session.SequenceId, moved.Id, DragOperation.Move, grab, new DragOptions(), session.Position));
            drag.Begin();
            int reloads = playback.Reloads.Count;
            drag.Move(grab + Time.FromSeconds(1), null);
            drag.Move(grab + Time.FromSeconds(2), null);
            drag.Move(grab + Time.FromSeconds(3), null);
            Assert.Equal(reloads + 1, playback.Reloads.Count);

            drag.Complete();

            Assert.Equal(reloads + 2, playback.Reloads.Count);
            Assert.Equal(grab + Time.FromSeconds(3), playback.Reloads[^1].GetSequence(session.SequenceId).GetClip(moved.Id).Clip.Start);
            NextFrame(frames);
            Assert.Equal(reloads + 2, playback.Reloads.Count);
        });
    }

    private static void NextFrame(List<Action> frames)
    {
        Action[] due = [.. frames];
        frames.Clear();
        foreach (Action action in due)
        {
            action();
        }
    }

    private static long Allocated(Func<object> copy)
    {
        long start = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(copy());
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    /// <summary>V1: 150 clips con su audio enlazado en A1 y dos efectos con keyframes; V2: 20 clips sueltos con hueco.</summary>
    private static string SaveProject(string directory)
    {
        var ids = new SequentialIdGenerator();
        var media = new MediaRef
        {
            Id = "m", Name = "m", Path = Path.Combine(directory, "m.mp4"), Duration = Time.FromSeconds(60), HasVideo = true, HasAudio = true,
            FrameRate = Rational.Fps30, Width = 1920, Height = 1080, SampleRate = 48000, AudioChannels = 2,
        };
        Project project = ProjectFactory.CreateDefault("Arrastre", ids, Rational.Fps30);
        project.Media.Add(media);
        Sequence sequence = project.Sequences[0];
        var ctx = new EditContext(project, sequence, ids);
        string main = sequence.VideoTracks[0].Id;
        string overlay = sequence.VideoTracks[1].Id;
        string audio = sequence.AudioTracks[0].Id;
        for (int i = 0; i < 150; i++)
        {
            TimelineEdits.AppendMedia(ctx, media.Id, Time.Zero, Time.FromSeconds(1 + (i % 4)), main, audio);
        }

        for (int i = 0; i < 20; i++)
        {
            TimelineEdits.InsertMedia(ctx, media.Id, Time.Zero, Time.FromSeconds(1), overlay, Time.FromSeconds(i * 5), PlacementMode.Overwrite);
        }

        foreach (Clip clip in sequence.VideoTracks[0].Clips)
        {
            clip.Effects.Add(new EffectInstance
            {
                Id = ids.NewId("fx"),
                Type = BuiltInEffects.Transform,
                Parameters = new()
                {
                    [BuiltInEffects.TransformParameters.Opacity] = new Parameter
                    {
                        Value = 1,
                        Keyframes = [new Keyframe(clip.SourceIn, 0), new Keyframe(clip.SourceIn + Time.FromSeconds(0.5), 1)],
                    },
                    [BuiltInEffects.TransformParameters.Scale] = new Parameter { Value = 1 },
                },
            });
            clip.Effects.Add(new EffectInstance
            {
                Id = ids.NewId("fx"),
                Type = BuiltInEffects.Color,
                Parameters = new()
                {
                    [BuiltInEffects.ColorParameters.Exposure] = new Parameter { Value = 0.2 },
                    [BuiltInEffects.ColorParameters.Saturation] = new Parameter { Value = 0.1 },
                },
            });
        }

        string path = Path.Combine(directory, "arrastre" + ProjectSerializer.FileExtension);
        File.WriteAllText(path, ProjectSerializer.Serialize(project));
        return path;
    }
}
