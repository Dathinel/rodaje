using System.Globalization;
using Rodaje.App.Services;
using Rodaje.App.Services.Format;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Rodaje.Tests.Fuzz;
using Xunit.Sdk;

namespace Rodaje.App.Tests.Fuzz;

/// <summary>
/// Proyectos con números extremos que el formato admite (tarea 220): tasas de 1 a 1000 fps con términos enormes, clips
/// pegados al final de la línea de tiempo (<see cref="Sequence.MaxTime"/>), marcadores y regiones en <see cref="long.MaxValue"/>, velocidades y curvas en sus topes, generadores
/// con el origen muy negativo, keyframes en <see cref="long.MinValue"/> y <see cref="long.MaxValue"/> y muchas pistas.
/// Se abren en la sesión, se editan con desplazamientos de un tick a casi todo el rango, se guardan y se vuelven a leer:
/// ninguna excepción escapa y el proyecto sigue siendo válido. Lo que el formato admite pero no se puede editar (una tasa
/// de 1/2^62, un clip que acaba cerca de <see cref="long.MaxValue"/>) lo rechaza el validador al abrir.
/// </summary>
[Trait("Categoria", "Fuzz")]
public class ExtremeProjectSessionTests
{
    private const long Big = 1L << 62;

    private static readonly Rational[] Rates =
    [
        new(1, 1), new(1000, 1), new(24000, 1001), new(Big, Big - 1), new((1000L << 40) - 1, 1L << 40), new(30000, 1001), new(25, 1), new(120, 1),
    ];

    /// <summary>En qué parte del caso va el fuzz y desde cuándo, para el mensaje de un cuelgue.</summary>
    private static string _phase = "";

    private static long _phaseStarted;

    /// <summary>
    /// En la CI de Windows, dos veces un caso «tardó más de 15 s» (semillas 20261013 y 20261015) que en Linux tarda de 17 a
    /// 460 ms (800 semillas, la más lenta 0,6 s): ningún paso recorre los números extremos. Si vuelve a pasar, el mensaje
    /// dice en qué tramo estaba (las esperas al pool al abrir y guardar, o un paso síncrono) y el estado del pool.
    /// </summary>
    [Fact]
    public Task ProyectosExtremos_SeEditanSinExcepciones() => OwnThread.Run(() =>
    {
        try
        {
            FuzzRunner.Run("proyecto con números extremos", 20261013, cases: 40, budget: TimeSpan.FromSeconds(10), minimumCases: 40, (seed, random) =>
            {
                Mark("preparar");
                using var directory = new TempDirectory();
                Project project = BuildProject(random, directory.Path);
                Assert.Empty(ModelValidator.Validate(project));
                string path = Path.Combine(directory.Path, "p" + ProjectSerializer.FileExtension);
                Mark("guardar el proyecto");
                ProjectSerializer.Save(project, path);
                UiThread.Run(() => EditAsync(random, directory.Path, path));
                Mark("borrar la carpeta");
            });
        }
        catch (XunitException ex) when (ex.Message.Contains("tardó más de", StringComparison.Ordinal))
        {
            double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _phaseStarted)).TotalSeconds;
            throw new XunitException(Invariant($"{ex.Message} Iba por «{Volatile.Read(ref _phase)}» desde hace {seconds:F1} s. {UiThread.PoolState()}"));
        }
    });

    private static void Mark(string phase)
    {
        Interlocked.Exchange(ref _phaseStarted, System.Diagnostics.Stopwatch.GetTimestamp());
        Volatile.Write(ref _phase, phase);
    }

    /// <summary>Una tasa de 1/2^62 (un cuadro de más de 400 años): empujar o recortar un cuadro lanzaba OverflowException.</summary>
    [Theory]
    [InlineData(1, Big)]
    [InlineData(Big, 1)]
    public void UnaTasaDeSecuenciaExtrema_NoSeAbre(long num, long den)
    {
        using var directory = new TempDirectory();
        string path = SaveOneClip(directory.Path, new Rational(num, den), start: 0);
        UiThread.Run(async () =>
        {
            (EditorSession session, List<EditorNotification> notes) = NewSession();

            Assert.False(await session.OpenProjectAsync(path));

            Assert.Contains(notes, n => n.Severity == NotificationSeverity.Error && n.Message.Contains("va de 1 a 1000", StringComparison.Ordinal));
        });
    }

    /// <summary>Un clip que acaba un segundo antes de <see cref="long.MaxValue"/>: empujarlo un cuadro lanzaba OverflowException.</summary>
    [Fact]
    public void UnClipQueAcabaCercaDelFinalDelRango_NoSeAbre()
    {
        using var directory = new TempDirectory();
        string path = SaveOneClip(directory.Path, Rational.Fps25, start: long.MaxValue - (2 * Time.TicksPerSecond));
        UiThread.Run(async () =>
        {
            (EditorSession session, List<EditorNotification> notes) = NewSession();

            Assert.False(await session.OpenProjectAsync(path));

            Assert.Contains(notes, n => n.Severity == NotificationSeverity.Error
                && n.Message.Contains("termina después del final de la línea de tiempo", StringComparison.Ordinal));
        });
    }

    /// <summary>Un clip que acaba justo en el final de la línea de tiempo se corta, se empuja y se recorta ahí sin lanzar.</summary>
    [Fact]
    public void UnClipQueAcabaEnElFinalDeLaLineaDeTiempo_SeEdita()
    {
        using var directory = new TempDirectory();
        string path = SaveOneClip(directory.Path, Rational.Ntsc30, start: Sequence.MaxTime.Ticks - Time.TicksPerSecond);
        UiThread.Run(async () =>
        {
            (EditorSession session, _) = NewSession();
            Assert.True(await session.OpenProjectAsync(path));
            Time end = session.Sequence.Duration;
            Assert.Equal(Sequence.MaxTime, end);

            session.UpdateTransport(PlaybackSnapshot.Empty with { Position = end, Duration = end });
            // El cuadro más cercano puede quedar después del final (fuera de todo clip), pero cabe en un long.
            Assert.True(session.EditPosition - end < Time.FromFrames(1, Rational.Ntsc30));
            session.Select(["c"]);
            Assert.False(session.NudgeSelection(1));
            session.SelectedEdge = new SelectedEdge("c", TrimEdge.Out, Ripple: false);
            Assert.False(session.TrimSelectedEdge(1));
            Assert.True(session.TrimSelectedEdge(-1));

            session.UpdateTransport(PlaybackSnapshot.Empty with { Position = end - Time.FromTicks(Time.TicksPerSecond / 2), Duration = end });
            Assert.True(session.SplitAtPlayhead());
            Assert.True(session.Undo());
            Assert.True(session.DeleteRightOfPlayhead());
            Assert.Empty(ModelValidator.Validate(session.Project));
            while (session.Undo())
            {
            }

            Assert.Equal(Sequence.MaxTime, session.Sequence.Duration);
        });
    }

    /// <summary>El selector de formato aceptaba cualquier tasa positiva y dejaba la secuencia con una que el validador rechaza.</summary>
    [Fact]
    public void CambiarElFormatoAUnaTasaExtrema_SeRechazaSinTocarElProyecto() => UiThread.Run(() =>
    {
        (EditorSession session, List<EditorNotification> notes) = NewSession();
        string before = ProjectSerializer.Serialize(session.Project);

        Assert.False(session.ChangeFormat(1920, 1080, new Rational(1, Big), ClipFit.Fit));

        Assert.Equal(before, ProjectSerializer.Serialize(session.Project));
        Assert.Contains(notes, n => n.Severity == NotificationSeverity.Warning && n.Message.Contains("de 1 a 1000", StringComparison.Ordinal));
    });

    private static (EditorSession Session, List<EditorNotification> Notes) NewSession()
    {
        var session = new EditorSession(new FuzzPlayback(), new FakeProber(), new FakeDialogs { Choice = UnsavedChangesChoice.Discard }, new FuzzIds());
        var notes = new List<EditorNotification>();
        session.Notified += (_, note) => notes.Add(note);
        return (session, notes);
    }

    /// <summary>Un video de un segundo en «v» desde <paramref name="start"/>, sin validar al guardar (puede no ser válido).</summary>
    private static string SaveOneClip(string directory, Rational rate, long start)
    {
        Project project = NewProject(directory, rate, videoTracks: 1, audioTracks: 1);
        project.Sequences[0].VideoTracks[0].Clips.Add(new Clip
        {
            Id = "c", MediaId = "m", Start = Time.FromTicks(start), SourceIn = Time.Zero, SourceOut = Time.FromTicks(Time.TicksPerSecond),
        });
        string path = Path.Combine(directory, "p" + ProjectSerializer.FileExtension);
        File.WriteAllText(path, ProjectSerializer.Serialize(project));
        return path;
    }

    private static Project NewProject(string directory, Rational rate, int videoTracks, int audioTracks)
    {
        var project = new Project { Id = "p", Name = "extremo" };
        project.Media.Add(new MediaRef
        {
            Id = "m", Name = "m", Path = Path.Combine(directory, "m.mp4"), Duration = Time.FromTicks(long.MaxValue), HasVideo = true, HasAudio = true,
            FrameRate = rate, Width = 1920, Height = 1080, SampleRate = 48000, AudioChannels = 2,
        });
        var sequence = new Sequence { Id = "seq", Name = "S", FrameRate = rate };
        for (int t = 0; t < videoTracks; t++)
        {
            sequence.VideoTracks.Add(new Track { Id = Invariant($"v{t}"), Kind = TrackKind.Video, Name = Invariant($"V{t + 1}") });
        }

        for (int t = 0; t < audioTracks; t++)
        {
            sequence.AudioTracks.Add(new Track { Id = Invariant($"a{t}"), Kind = TrackKind.Audio, Name = Invariant($"A{t + 1}") });
        }

        if (sequence.VideoTracks.Count > 0)
        {
            sequence.VideoTracks[0].Id = "v";
        }

        project.Sequences.Add(sequence);
        return project;
    }

    private static Time Pick(Random random, params long[] ticks) => Time.FromTicks(ticks[random.Next(ticks.Length)]);

    /// <summary>Un proyecto válido con valores en sus topes: pistas y clips de sobra, clips pegados al final, velocidades y curvas extremas.</summary>
    private static Project BuildProject(Random random, string directory)
    {
        long max = Sequence.MaxTime.Ticks;
        long extent = RetimeMath.MaxExtent;
        bool many = random.Next(3) == 0;
        Project project = NewProject(directory, Rates[random.Next(Rates.Length)], random.Next(1, many ? 40 : 4), random.Next(1, many ? 12 : 3));
        Sequence sequence = project.Sequences[0];
        int n = 0;
        foreach (Track track in sequence.AllTracks)
        {
            int count = random.Next(1, random.Next(4) == 0 ? 60 : 5);
            long at = Pick(random, 0, max / 2, max - (count * (extent + Time.TicksPerSecond)), max - (count * 4)).Ticks;
            for (int i = 0; i < count && at < max; i++)
            {
                long length = Math.Min(max - at, Pick(random, 1, 2, 23_543_520, Time.TicksPerSecond, extent, 1L << 40, random.NextInt64(1, extent)).Ticks);
                Clip clip = NewClip(random, track, Invariant($"c{n++}"), Time.FromTicks(at), length);
                if (random.Next(3) == 0)
                {
                    var transform = new EffectInstance { Id = Invariant($"e{n}"), Type = BuiltInEffects.Transform };
                    transform.Parameters[BuiltInEffects.TransformParameters.Opacity] = new Parameter
                    {
                        Value = 1,
                        Keyframes = [new Keyframe(Time.FromTicks(long.MinValue), 0), new Keyframe(Time.Zero, 0.5), new Keyframe(Time.FromTicks(long.MaxValue), 1)],
                    };
                    clip.Effects.Add(transform);
                }

                track.Clips.Add(clip);
                bool touching = random.Next(2) == 0;
                if (touching && i > 0 && track.Kind == TrackKind.Video && random.Next(2) == 0 && track.Clips[^2].End == clip.Start)
                {
                    long shortest = Math.Min(track.Clips[^2].Duration.Ticks, clip.Duration.Ticks);
                    track.Transitions.Add(new TransitionInstance
                    {
                        Id = Invariant($"t{n}"), Type = BuiltInTransitions.Crossfade, From = track.Clips[^2].Id, To = clip.Id, Duration = Time.FromTicks(Math.Max(1, shortest / 2)),
                    });
                }

                long gap = touching ? 0 : Pick(random, 1, Time.TicksPerSecond, 1L << 50).Ticks;
                at = Math.Min(max, clip.End.Ticks + gap);
            }
        }

        sequence.Markers.Add(new Marker { Id = "mk0", Time = Time.Zero, Name = "inicio" });
        sequence.Markers.Add(new Marker { Id = "mk1", Time = Time.FromTicks(long.MaxValue), Name = "final" });
        sequence.Regions.Add(new Region { Id = "rg", Start = Time.Zero, End = Time.FromTicks(long.MaxValue), Name = "todo" });
        return project;
    }

    private static Clip NewClip(Random random, Track track, string id, Time start, long length)
    {
        var clip = new Clip { Id = id, Start = start };
        int kind = random.Next(5);
        if (kind == 0 && track.Kind == TrackKind.Video)
        {
            // Un generador tiene un origen virtual sin límites: el punto de entrada puede ser muy negativo.
            clip.Generator = new ClipGenerator { Text = "t", Style = new TextStyle() };
            clip.SourceIn = Pick(random, 0, -length, long.MinValue / 2, -Big);
            clip.SourceOut = clip.SourceIn + Time.FromTicks(length);
            return clip;
        }

        clip.MediaId = "m";
        clip.SourceIn = Pick(random, 0, 1, long.MaxValue / 2, long.MaxValue - length - 1);
        clip.SourceOut = clip.SourceIn + Time.FromTicks(length);
        if (kind < 3)
        {
            return clip;
        }

        long duration = Math.Min(length, RetimeMath.MaxExtent - 1);
        var speed = new ClipSpeed();
        if (track.Kind == TrackKind.Video && random.Next(6) == 0)
        {
            speed.Freeze = true;
        }
        else if (kind == 3)
        {
            speed.Rate = new[] { RetimeMath.MinRate, RetimeMath.MaxRate, new Rational(RetimeMath.MaxRateTerm - 1, RetimeMath.MaxRateTerm), new Rational(1001, 2400) }[random.Next(4)];
            speed.Reverse = random.Next(2) == 0;
        }
        else
        {
            speed.Curve = new SpeedCurve
            {
                Span = Pick(random, 1, RetimeMath.MaxExtent, duration, Math.Max(1, duration / 3)),
                Points = random.Next(2) == 0
                    ? [new(0, RetimeMath.MinHundredths), new(SpeedCurve.AtScale, RetimeMath.MaxHundredths)]
                    : [new(0, RetimeMath.MaxHundredths), new(1, RetimeMath.MinHundredths), new(SpeedCurve.AtScale - 1, RetimeMath.MaxHundredths), new(SpeedCurve.AtScale, RetimeMath.MinHundredths)],
            };
        }

        if (!speed.Freeze)
        {
            speed.Offset = Pick(random, 0, 1, RetimeMath.MaxExtent - duration, -(RetimeMath.MaxExtent - duration));
        }

        // Con el origen pegado al final del medio, una velocidad puede pedir material que no hay: el clip se queda a 1×.
        ClipTimeMap map;
        try
        {
            map = ClipTimeMap.Create(start, Time.FromTicks(duration), clip.SourceIn, speed);
        }
        catch (OverflowException)
        {
            return clip;
        }

        if (map.SourceIn >= Time.Zero && map.SourceOut.Ticks < long.MaxValue)
        {
            clip.Speed = speed;
            map.ApplyTo(clip);
        }

        return clip;
    }

    /// <summary>Cien acciones al azar sobre el proyecto abierto; después lo guarda y lo vuelve a leer.</summary>
    private static async Task EditAsync(Random random, string directory, string path)
    {
        (EditorSession session, _) = NewSession();
        Mark("abrir");
        Assert.True(await session.OpenProjectAsync(path));
        string seq = session.SequenceId;
        for (int step = 0; step < 100; step++)
        {
            List<Clip> clips = [.. session.Sequence.AllTracks.SelectMany(t => t.Clips)];
            Clip? clip = clips.Count == 0 ? null : clips[random.Next(clips.Count)];
            string id = clip?.Id ?? "ninguno";
            long end = session.Sequence.Duration.Ticks;
            Time at = Pick(random, 0, end, end / 2, Math.Max(0, end - 1), clip?.Start.Ticks ?? 0, clip is null ? 0 : clip.Start.Ticks + (clip.Duration.Ticks / 2), clip?.End.Ticks ?? 0);
            Time delta = Pick(random, 1, -1, 23_543_520, -23_543_520, 3600 * Time.TicksPerSecond, -3600 * Time.TicksPerSecond, 1L << 61, -(1L << 61), long.MaxValue, long.MinValue + 1);
            TrimEdge edge = random.Next(2) == 0 ? TrimEdge.In : TrimEdge.Out;
            int frames = new[] { 1, -1, 1000, -1000, int.MaxValue, int.MinValue }[random.Next(6)];
            int action = random.Next(26);
            string what = Invariant($"paso {step}: acción {action} sobre «{id}», en {at.Ticks}, Δ {delta.Ticks}, {frames} cuadros");
            Mark(what);
            try
            {
                Act(session, seq, action, id, at, delta, edge, frames, random);
            }
            catch (Exception ex)
            {
                throw new XunitException($"{what}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }

            IReadOnlyList<string> errors = ModelValidator.Validate(session.Project);
            Assert.True(errors.Count == 0, $"{what}: deja el proyecto inválido: {string.Join(" / ", errors)}");
        }

        string saved = Path.Combine(directory, "guardado" + ProjectSerializer.FileExtension);
        Mark("guardar como");
        Assert.True(await session.SaveAsAsync(saved));
        Mark("volver a leer");
        Assert.Empty(ModelValidator.Validate(ProjectSerializer.Load(saved)));
    }

    private static void Act(EditorSession session, string seq, int action, string id, Time at, Time delta, TrimEdge edge, int frames, Random random)
    {
        string video = session.Sequence.VideoTracks[0].Id;
        string audio = session.Sequence.AudioTracks[0].Id;
        _ = action switch
        {
            0 => Seek(session, at),
            1 => session.SplitAtPlayhead(),
            2 => session.SplitAllTracksAtPlayhead(),
            3 => session.DeleteLeftOfPlayhead(),
            4 => session.DeleteRightOfPlayhead(),
            5 => Nudge(session, id, frames),
            6 => TrimEdgeBy(session, id, edge, random.Next(2) == 0, frames),
            7 => session.Undo(),
            8 => session.Redo(),
            9 => session.Execute(Edits.Trim(seq, id, edge, delta)),
            10 => session.Execute(Edits.RippleTrim(seq, id, edge, delta, (RippleScope)random.Next(3))),
            11 => session.Execute(Edits.Roll(seq, id, delta)),
            12 => session.Execute(Edits.Slip(seq, id, delta)),
            13 => session.Execute(Edits.Slide(seq, id, delta)),
            14 => session.Execute(Edits.Move(seq, id, at, null, (PlacementMode)random.Next(3))),
            15 => session.Execute(Edits.MoveClips(seq, [id], delta)),
            16 => session.Execute(Edits.RateStretch(seq, id, edge, delta)),
            17 => session.Execute(Edits.SetSpeed(seq, id, new[] { RetimeMath.MinRate, RetimeMath.MaxRate, new Rational(1, 1), new Rational(3, 7) }[random.Next(4)], RippleScope.AllTracks)),
            18 => session.Execute(Edits.SetReverse(seq, id, random.Next(2) == 0)),
            19 => session.Execute(Edits.Split(seq, id, at)),
            20 => session.Execute(Edits.InsertMedia(seq, "m", Time.Zero, Pick(random, 1, Time.TicksPerSecond, 1L << 61), video, at, (PlacementMode)random.Next(3), audio)),
            21 => session.Execute(Edits.AppendMedia(seq, "m", Time.Zero, Pick(random, 1, Time.TicksPerSecond, 1L << 61), video)),
            22 => session.Execute(Edits.CloseAllGaps(seq, session.Sequence.AllTracks.Select(t => t.Id), RippleScope.AllTracks)),
            23 => DeleteAll(session),
            24 => session.Execute(Edits.AddMarker(seq, at)),
            _ => session.Execute(Edits.FreezeFrame(seq, id, at)),
        };
    }

    private static bool Seek(EditorSession session, Time at)
    {
        session.Seek(at);
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = at, Duration = session.Sequence.Duration });
        _ = session.EditPosition;
        return true;
    }

    private static bool Nudge(EditorSession session, string id, int frames)
    {
        session.Select([id]);
        return session.NudgeSelection(frames);
    }

    private static bool TrimEdgeBy(EditorSession session, string id, TrimEdge edge, bool ripple, int frames)
    {
        session.SelectedEdge = new SelectedEdge(id, edge, ripple);
        return session.TrimSelectedEdge(frames);
    }

    private static bool DeleteAll(EditorSession session)
    {
        session.SelectAll();
        return session.DeleteSelection();
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
