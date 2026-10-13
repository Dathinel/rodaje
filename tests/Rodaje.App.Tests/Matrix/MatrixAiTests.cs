using System.Diagnostics;
using System.Globalization;
using Rodaje.Analysis;
using Rodaje.Analysis.Ducking;
using Rodaje.Analysis.Rhythm;
using Rodaje.Analysis.Silence;
using Rodaje.Analysis.Video;
using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Titles;
using Rodaje.App.Services.Modules;
using Rodaje.App.Services.Translate;
using Rodaje.App.Services.Voice;
using Rodaje.App.Tests.Modules;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Analysis;
using Rodaje.App.Views.Raw;
using Rodaje.App.Views.Subtitles;
using Rodaje.App.Views.Vision;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.Matrix;

/// <summary>
/// La IA local y los análisis con los formatos de la matriz (<c>Categoria=Matriz</c>, solo con <c>RODAJE_PRUEBAS_LARGAS=1</c>):
/// cada medio, con 12 s de voz real, se abre en una sesión de verdad y pasa por subtítulos, reducir ruido, voz y ducking,
/// silencios, ritmo, estabilizar, color automático, quitar fondo y reencuadre. Cada resultado entra como un paso del
/// historial y al final se deshace todo hasta el proyecto de partida. La tabla queda en <c>TestResults\matriz\ia.md</c>.
/// </summary>
[Collection(MatrixGroup.Name)]
[Trait("Categoria", "Matriz")]
public sealed class MatrixAiTests(ITestOutputHelper output) : IDisposable
{
    private const double Seconds = 12;
    private static readonly string[] Aac = ["-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2"];
    private static readonly string[] Nvenc = ["-c:v", "h264_nvenc", "-preset", "p4", "-cq", "20", "-g", "30", "-pix_fmt", "yuv420p"];

    private readonly TempDirectory _folder = new();

    internal static IReadOnlyList<MatrixCase> Cases { get; } = Voice(
    [
        new("ia-mp4-h264-aac", "MP4", "H.264", "AAC estéreo 48 kHz", ".mp4") { Video = Nvenc, Audio = Aac, Requires = "h264_nvenc" },
        new("ia-mkv-hevc10-opus", "MKV", "HEVC Main 10", "Opus estéreo", ".mkv")
        {
            Video = ["-c:v", "hevc_nvenc", "-preset", "p4", "-cq", "22", "-g", "30", "-profile:v", "main10", "-pix_fmt", "p010le"],
            Audio = ["-c:a", "libopus", "-b:a", "96k", "-ac", "2"], Requires = "hevc_nvenc",
        },
        new("ia-webm-vp9-opus", "WebM", "VP9", "Opus estéreo", ".webm")
        {
            Video = ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-crf", "34", "-b:v", "0", "-g", "30", "-pix_fmt", "yuv420p"],
            Audio = ["-c:a", "libopus", "-b:a", "96k", "-ac", "2"], Requires = "libvpx-vp9",
        },
        new("ia-mov-prores422-pcm24", "MOV", "ProRes 422 HQ", "PCM 24 estéreo 48 kHz", ".mov")
        {
            Video = ["-c:v", "prores_ks", "-profile:v", "3", "-pix_fmt", "yuv422p10le"], Audio = ["-c:a", "pcm_s24le", "-ar", "48000", "-ac", "2"],
        },
        new("ia-mov-prores4444-pcm96", "MOV", "ProRes 4444 12 bits", "PCM 24 estéreo 96 kHz", ".mov")
        {
            Video = ["-c:v", "prores_ks", "-profile:v", "4", "-pix_fmt", "yuva444p10le"], Audio = ["-c:a", "pcm_s24le", "-ar", "96000", "-ac", "2"],
        },
        new("ia-mxf-dnxhr-pcm", "MXF", "DNxHR HQ 1080p25", "PCM 16 estéreo 48 kHz", ".mxf")
        {
            Width = 1920, Height = 1080, Rate = new Rational(25, 1),
            Video = ["-c:v", "dnxhd", "-profile:v", "dnxhr_hq", "-pix_fmt", "yuv422p"], Audio = ["-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2"],
        },
        new("ia-avi-mjpeg-mp3mono", "AVI", "MJPEG", "MP3 mono 44,1 kHz", ".avi")
        {
            Video = ["-c:v", "mjpeg", "-q:v", "4", "-pix_fmt", "yuvj420p"], Audio = ["-c:a", "libmp3lame", "-b:a", "96k", "-ar", "44100", "-ac", "1"],
        },
        new("ia-mp4-rotado-aac51", "MP4", "H.264 1080p con rotación 90°", "AAC 5.1", ".mp4")
        {
            Width = 1920, Height = 1080, Rotation = 90, Video = Nvenc, Audio = ["-c:a", "aac", "-b:a", "320k", "-ar", "48000", "-ac", "6"], Requires = "h264_nvenc",
        },
        new("ia-wav-pcm24-96", "WAV", "—", "PCM 24 estéreo 96 kHz", ".wav") { Kind = MatrixKind.AudioOnly, Audio = ["-c:a", "pcm_s24le", "-ar", "96000", "-ac", "2"] },
        new("ia-flac-mono44", "FLAC", "—", "FLAC mono 44,1 kHz", ".flac") { Kind = MatrixKind.AudioOnly, Audio = ["-c:a", "flac", "-ar", "44100", "-ac", "1"] },
    ]);

    /// <summary>Pitidos cada medio segundo durante 12 s: la música del ducking y del ritmo (120 por minuto).</summary>
    private static readonly MatrixCase Music = new("ia-musica-120", "WAV", "—", "PCM 16 estéreo", ".wav")
    {
        Kind = MatrixKind.AudioOnly, Seconds = Seconds, Audio = ["-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2"],
    };

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (MatrixCase c in Cases)
        {
            data.Add(c.Name);
        }

        return data;
    }

    public void Dispose() => _folder.Dispose();

    [MatrixAiTheory]
    [MemberData(nameof(Names))]
    public void Each_ai_tool_accepts_the_format(string name) => UiThread.Run(async () =>
    {
        MatrixCase c = Cases.Single(x => x.Name == name);
        var row = new AiRow(c.Name, $"{c.Container}: {c.VideoLabel} / {c.AudioLabel}");
        string path = Generate(c);
        string music = Generate(Music);

        var session = new EditorSession(new FakePlayback(), new FfmpegMediaProber(), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([path]);
        string original = ProjectSerializer.Serialize(session.Project);
        int startSteps = session.History.Count;
        MediaRef media = Assert.Single(session.Project.Media);
        Clip? video = session.Sequence.VideoTracks.SelectMany(t => t.Clips).FirstOrDefault();
        Clip audio = session.Sequence.AudioTracks.SelectMany(t => t.Clips).First();
        Assert.Equal(c.HasVideo, video is not null);

        await using ModuleManager manager = Modules();
        var failures = new List<ModuleFailure>();
        manager.Failed += (_, f) => failures.Add(f);
        var proposals = new ModuleProposals(session);

        await row.StepAsync("Subtítulos", session, async () =>
        {
            ModuleDescriptor module = Require(manager, AutoSubtitles.Function);
            Transcript transcript = await AutoSubtitles.RunAsync(manager, module, path, AutoSubtitles.Window(audio), "es", "Subtítulos", CancellationToken.None);
            IReadOnlyList<SpeechSegment> segments = AutoSubtitles.ToSequence(audio, transcript);
            int words = segments.Sum(s => s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.True(words >= 10, $"Solo {words} palabras en 12 s de voz.");
            Execute(session, AutoSubtitles.Command(session.SequenceId, segments, SubtitleStyles.NewSubtitle()));
            return Invariant($"{segments.Count} subtítulos, {words} palabras: «{Shorten(segments[0].Text)}»");
        });

        await row.StepAsync("Ruido", session, async () =>
        {
            ModuleDescriptor module = Require(manager, NoiseReduction.Function);
            CleanAudio clean = await NoiseReduction.RunAsync(manager, module, path, NoiseReduction.Window(audio, media.Duration), _folder.Path, "Reducir ruido", CancellationToken.None);
            MediaRef cleaned = NoiseReduction.Media("limpio", clean, media.Name + " (sin ruido)");
            Execute(session, NoiseReduction.Command(session.SequenceId, audio.Id, cleaned, clean.Offset));
            audio = session.Sequence.FindClip(audio.Id)!.Value.Clip;
            return Invariant($"{clean.Duration.ToSeconds():0.0} s limpios");
        });

        Clip musicClip = AddMusic(session, music);
        await row.StepAsync("Voz y ducking", session, async () =>
        {
            var detector = new ModuleVoiceDetector(manager, new EnergyVoiceDetector());
            Assert.True(detector.UsesModule, "Sin modulo-audio-ia: respondería el detector de energía.");
            IVoiceActivitySession detection = detector.Begin(ClipAudioSource.SampleRate, ClipAudioSource.Channels);
            (Time sourceIn, Time sourceOut) = ClipAudioMap.SourceWindow(audio);
            Clip voiceClip = audio;
            string voicePath = session.Project.FindMedia(voiceClip.MediaId)!.Path;
            await Task.Run(() => ClipAudioSource.Decode(voicePath, sourceIn, sourceOut, detection, null, CancellationToken.None));
            List<TimeRange> found = [.. (await detection.FinishAsync()).Select(r => ClipAudioMap.ToTimeline(voiceClip, r)).Select(r => new TimeRange(r.Start.ToSeconds(), r.End.ToSeconds()))];
            List<TimeRange> voice = TimeRange.Merge(found);
            Assert.True(voice.Sum(r => r.Duration) > 4, $"Poca voz: {string.Join(", ", voice)}");
            List<GainPoint> plan = DuckingPlanner.Plan(voice, new DuckingOptions());
            Execute(session, Edits.Duck(session.SequenceId, [musicClip.Id], [.. plan.Select(p => (Time.FromSeconds(Math.Max(0, p.Time)), p.OffsetDb))]));
            return Invariant($"{voice.Count} tramos, {voice.Sum(r => r.Duration):0.0} s de voz; {plan.Count} puntos");
        });

        await row.StepAsync("Ritmo", session, async () =>
        {
            var onsets = new OnsetEnvelope(ClipAudioSource.SampleRate, ClipAudioSource.Channels);
            (Time sourceIn, Time sourceOut) = ClipAudioMap.SourceWindow(musicClip);
            await Task.Run(() => ClipAudioSource.Decode(music, sourceIn, sourceOut, onsets, null, CancellationToken.None));
            BeatAnalysis beats = BeatTracker.Track(onsets);
            Assert.InRange(beats.Bpm, 115, 125);
            Execute(session, Edits.AddBeatMarkers(session.SequenceId, [.. beats.Beats.Select(b => ClipAudioMap.ToTimeline(musicClip, b))]));
            return Invariant($"{beats.Bpm:0} por minuto, {beats.Beats.Count} pulsos");
        });

        if (video is not null)
        {
            await VideoStepsAsync(c, session, manager, proposals, row, video, media);
        }

        await row.StepAsync("Silencios", session, async () =>
        {
            var levels = new LevelEnvelope(ClipAudioSource.SampleRate, ClipAudioSource.Channels);
            (Time sourceIn, Time sourceOut) = ClipAudioMap.SourceWindow(audio);
            string voicePath = session.Project.FindMedia(audio.MediaId)!.Path;
            await Task.Run(() => ClipAudioSource.Decode(voicePath, sourceIn, sourceOut, levels, null, CancellationToken.None));
            SilenceAnalysis silences = SilenceDetector.Detect(levels, new SilenceOptions());
            List<(Time Start, Time End)> spans = ClipAudioMap.ToTimeline(audio, silences.Cuts);
            if (spans.Count == 0)
            {
                return "sin silencios que quitar";
            }

            Execute(session, Edits.RemoveSilences(session.SequenceId, audio.Id, spans, ripple: true));
            return Invariant($"{spans.Count} silencios, {spans.Sum(s => (s.End - s.Start).ToSeconds()):0.0} s");
        });

        row.Failures.AddRange(failures.Select(f => $"{name} [módulo]: {f.Message}"));
        AiTable.Save(row);
        output.WriteLine(row.ToMarkdown());
        Assert.True(row.Failures.Count == 0, string.Join(Environment.NewLine, row.Failures));
        while (session.History.Count > startSteps)
        {
            Assert.True(session.Undo());
        }

        Assert.Equal(original, ProjectSerializer.Serialize(session.Project));
    }, TimeSpan.FromMinutes(20));

    /// <summary>Lo que no depende del formato: traducir subtítulos, voz sintética y CinemaDNG (que luego se estabiliza y se colorea).</summary>
    [MatrixAiFact]
    public void Translation_voice_and_cinemadng_go_into_the_project() => UiThread.Run(async () =>
    {
        var row = new AiRow("ia-texto-voz-raw", "Traducción, texto a voz y CinemaDNG");
        string path = Generate(Cases[0]);
        var session = new EditorSession(new FakePlayback(), new FfmpegMediaProber(), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([path]);
        Clip audio = session.Sequence.AudioTracks.SelectMany(t => t.Clips).First();
        await using ModuleManager manager = Modules();
        var failures = new List<ModuleFailure>();
        manager.Failed += (_, f) => failures.Add(f);

        Transcript transcript = await AutoSubtitles.RunAsync(manager, Require(manager, AutoSubtitles.Function), path, AutoSubtitles.Window(audio), "es", "Subtítulos", CancellationToken.None);
        Execute(session, AutoSubtitles.Command(session.SequenceId, AutoSubtitles.ToSequence(audio, transcript), SubtitleStyles.NewSubtitle()));

        await row.StepAsync("Traducción", session, async () =>
        {
            Track track = SubtitleTrackTranslation.TranslatableTracks(session.Sequence)[0];
            IReadOnlyList<SubtitleText> texts = SubtitleTrackTranslation.Collect(track);
            TrackTranslation result = await new ModuleSubtitleTranslator(manager).TranslateAsync(texts, "es", "en", "Traducir", CancellationToken.None);
            Assert.Empty(result.Untranslated);
            var english = new TranslationLanguage("en", "Inglés");
            Execute(session, SubtitleTrackTranslation.Command(session.SequenceId, track.Id, result.Texts,
                SubtitleTrackTranslation.TrackName(session.Sequence, track, english)));
            return Invariant($"{result.Texts.Count} textos: «{Shorten(result.Texts.Values.First())}»");
        });

        await row.StepAsync("Texto a voz", session, async () =>
        {
            var settings = new VoiceSettings("Hola. Esta voz sale del módulo de texto a voz de Rodaje, sin internet.");
            SynthesizedVoice voice = await new ModuleVoiceSynthesizer(manager).SynthesizeAsync(settings, "Voz", CancellationToken.None);
            string wav = VoiceFiles.Save(_folder.Path, settings, voice);
            Assert.True(session.InsertGeneratedVoice(wav, settings, voice.Samples.Length, voice.SampleRate));
            Assert.True(new FfmpegMediaProber().Probe(wav).IsUsable);
            return Invariant($"{voice.Samples.Length / (double)voice.SampleRate:0.0} s a {voice.SampleRate} Hz");
        });

        await row.StepAsync("CinemaDNG", session, async () =>
        {
            if (Require(manager, CinemaDngImport.Function) is var module && !File.Exists(Path.Combine(Path.GetDirectoryName(module.EntryPath)!, "libraw.dll")))
            {
                throw new InvalidOperationException("modulo-raw sin libraw.dll (build\\preparar-entorno.ps1).");
            }

            string folder = Path.Combine(_folder.Path, "Toma ñ");
            Directory.CreateDirectory(folder);
            byte[] dng = (SyntheticDng.Chart(640, 360) with { FrameRate = (25, 1) }).Write();
            for (int i = 0; i < 25; i++)
            {
                await File.WriteAllBytesAsync(Path.Combine(folder, Invariant($"Toma_{i:00000}.dng")), dng);
            }

            CinemaDngResult result = await CinemaDngImport.RunAsync(manager, module, FfmpegExecutable.Locate(), CinemaDngImport.Scan(folder)!,
                new CinemaDngOptions(), Path.Combine(_folder.Path, "medios"), "CinemaDNG");
            ImportResult imported = await session.ImportMediaAsync([result.Path]);
            Assert.Single(imported.MediaIds);
            return Invariant($"{result.Frames} cuadros {result.Width}×{result.Height} → ProRes 422 HQ");
        });

        // El ProRes 4:2:2 de CinemaDNG se estabiliza y se colorea como cualquier clip.
        MediaRef raw = session.Project.Media[^1];
        session.AppendToTimeline(raw.Id);
        await row.StepAsync("Estabilizar CinemaDNG", session, async () =>
        {
            Clip clip = session.Sequence.VideoTracks.SelectMany(t => t.Clips).Last(x => x.MediaId == raw.Id);
            (Time sourceIn, Time sourceOut) = ClipAudioMap.SourceWindow(clip);
            (string key, MotionTrack track) = await Task.Run(() => ClipVideoSource.Motion(raw.Path, sourceIn, sourceOut, new StabilizationStore(_folder.Path), null, CancellationToken.None));
            Execute(session, VideoAnalysisEdits.Stabilize(session.SequenceId, clip.Id, key, 0.5, autoCrop: true));
            return Invariant($"{track.Motions.Count} cuadros analizados");
        });

        row.Failures.AddRange(failures.Select(f => $"[módulo]: {f.Message}"));
        AiTable.Save(row);
        output.WriteLine(row.ToMarkdown());
        Assert.True(row.Failures.Count == 0, string.Join(Environment.NewLine, row.Failures));
    }, TimeSpan.FromMinutes(20));

    private async Task VideoStepsAsync(MatrixCase c, EditorSession session, ModuleManager manager, ModuleProposals proposals, AiRow row, Clip video, MediaRef media)
    {
        await row.StepAsync("Estabilizar", session, async () =>
        {
            (Time sourceIn, Time sourceOut) = ClipAudioMap.SourceWindow(video);
            (string key, MotionTrack track) = await Task.Run(() => ClipVideoSource.Motion(media.Path, sourceIn, sourceOut, new StabilizationStore(_folder.Path), null, CancellationToken.None));
            Execute(session, VideoAnalysisEdits.Stabilize(session.SequenceId, video.Id, key, 0.5, autoCrop: true));
            return Invariant($"{track.Motions.Count} cuadros");
        });

        await row.StepAsync("Color automático", session, async () =>
        {
            ColorEstimate estimate = await ClipVideoSource.AnalyzeColorAsync(session, video, null, CancellationToken.None);
            ColorSettings settings = AutoColor.Auto(estimate);
            Execute(session, VideoAnalysisEdits.Color(session.SequenceId, video.Id, settings, levels: true));
            return Invariant($"exposición {settings.Exposure:+0.00;-0.00}, temperatura {settings.Temperature:+0.00;-0.00}");
        });

        // Los análisis por cuadro, sobre los 3 primeros segundos del clip.
        var shortClip = new Clip { Id = video.Id, MediaId = video.MediaId, Start = video.Start, SourceIn = video.SourceIn, SourceOut = video.SourceIn + Time.FromSeconds(3) };
        await row.StepAsync("Quitar fondo", session, async () =>
        {
            ModuleDescriptor module = Require(manager, VisionTools.RemoveBackgroundFunction);
            MatteVideo matte = await VisionTools.RemoveBackgroundAsync(manager, module, FfmpegExecutable.Locate(), media.Path, shortClip, media.FrameRate!.Value,
                highQuality: false, _folder.Path, "Quitar fondo", CancellationToken.None);
            Assert.True(matte.Frames > 0 && File.Exists(matte.Path));
            string sequenceId = session.SequenceId;
            proposals.Show(new ModuleProposal("Quitar fondo", sequenceId, [], _ => [Edits.SetImageMask(sequenceId, video.Id, LutReference.ForFile(matte.Path), matte.Start)]));
            Assert.True(proposals.Apply());
            return Invariant($"{matte.Frames} cuadros de máscara {matte.Width}×{matte.Height}");
        });

        await row.StepAsync("Reencuadre", session, async () =>
        {
            ModuleDescriptor module = Require(manager, VisionTools.ReframeFunction);
            ReframePlan plan = await VisionTools.ReframeAsync(manager, module, media.Path, shortClip, 9 / 16.0, faces: false, 0.5, "Reencuadre", CancellationToken.None);
            Sequence sequence = session.Sequence;
            TransformKey[] keys = [.. plan.Keys.Select(k => VisionTools.ToTransform(k, media.Width, media.Height, sequence.Width, sequence.Height))];
            string sequenceId = session.SequenceId;
            proposals.Show(new ModuleProposal("Reencuadre", sequenceId, [], _ => [Edits.SetTransformKeys(sequenceId, video.Id, keys)]));
            Assert.True(proposals.Apply());
            return Invariant($"{plan.Frames} cuadros analizados, {keys.Length} keyframes");
        });

        _ = c;
    }

    /// <summary>Ejecuta el comando como la app; si no entra, falla con el aviso que la app mostraría.</summary>
    private static void Execute(EditorSession session, ICommand command)
    {
        EditorNotification? notice = null;
        void Capture(object? sender, EditorNotification n) => notice = n;
        session.Notified += Capture;
        try
        {
            Assert.True(session.Execute(command), $"«{command.Description}» no se aplicó: {notice?.Title}: {notice?.Message}");
        }
        finally
        {
            session.Notified -= Capture;
        }
    }

    private static Clip AddMusic(EditorSession session, string music)
    {
        var probe = new FfmpegMediaProber().Probe(music);
        var media = new MediaRef { Id = "musica", Path = music };
        probe.Info!.ApplyTo(media);
        Time end = media.Duration;
        var place = new SequenceEditCommand("Música", session.SequenceId, ctx =>
        {
            string trackId = TimelineEdits.AddTrack(ctx, TrackKind.Audio, ctx.Sequence.AudioTracks.Count, "Música");
            return TimelineEdits.InsertMedia(ctx, media.Id, Time.Zero, end, trackId, Time.Zero, PlacementMode.Strict);
        });
        Execute(session, new CompositeCommand("Música", [new AddMediaCommand(media), place]));
        return session.Sequence.FindClip(place.CreatedIds[0])!.Value.Clip;
    }

    private static string Generate(MatrixCase c)
    {
        (string? path, string? problem) = MatrixMedia.GetOrCreate(FfmpegTool.Default!, c);
        Assert.True(path is not null, problem);
        return path!;
    }

    private static ModuleManager Modules()
    {
        var manager = new ModuleManager(new ModuleManagerOptions { Roots = [new ModuleRoot(AudioAiCorpus.ModulesFolder!, ModuleOrigin.Development)] });
        manager.Refresh();
        return manager;
    }

    private static ModuleDescriptor Require(ModuleManager manager, string function) =>
        manager.FindByFunction(function) ?? throw new InvalidOperationException($"No hay módulo compilado con «{function}» en modules\\ ({string.Join(" ", manager.Problems)}).");

    private static IReadOnlyList<MatrixCase> Voice(IReadOnlyList<MatrixCase> cases) =>
        [.. cases.Select(c => c with { Seconds = Seconds, Voice = AudioAiCorpus.Voice, VoiceOffset = 20 })];

    private static string Shorten(string text) => text.Length <= 60 ? text.Trim() : text[..60].Trim() + "…";

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Una fila de la tabla de IA: el resultado y el tiempo de cada paso, y que cada uno sea un solo paso del historial.</summary>
internal sealed class AiRow(string name, string format)
{
    private readonly List<(string Step, string Text)> _cells = [];

    public string Name { get; } = name;
    public List<string> Failures { get; } = [];

    public async Task StepAsync(string step, EditorSession session, Func<Task<string>> body)
    {
        int before = session.History.Count;
        var watch = Stopwatch.StartNew();
        try
        {
            string text = await body();
            int steps = session.History.Count - before;
            if (steps > 1)
            {
                throw new InvalidOperationException($"dejó {steps} pasos en el historial");
            }

            _cells.Add((step, string.Create(CultureInfo.InvariantCulture, $"{text}; {watch.Elapsed.TotalSeconds:0.0} s")));
        }
#pragma warning disable CA1031 // La fila anota cualquier fallo y sigue con el siguiente análisis.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            string message = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
            Failures.Add($"{Name} [{step}]: {message}");
            _cells.Add((step, "FALLA: " + message));
        }
    }

    public string ToMarkdown() =>
        $"| {Name} | {format} | {(Failures.Count == 0 ? "ok" : "FALLA")} | {string.Join("<br>", _cells.Select(c => $"**{c.Step}:** {c.Text.Replace('|', '/')}"))} |";
}

internal static class AiTable
{
    private static readonly Lock Gate = new();

    public static void Save(AiRow row)
    {
        lock (Gate)
        {
            string rows = Path.Combine(MatrixTable.Folder, "filas-ia");
            Directory.CreateDirectory(rows);
            File.WriteAllText(Path.Combine(rows, row.Name + ".md"), row.ToMarkdown());
            var lines = new List<string> { "| Caso | Formato | Estado | Pasos (resultado; tiempo) |", "|---|---|---|---|" };
            lines.AddRange(Directory.GetFiles(rows, "*.md").Order(StringComparer.Ordinal).Select(File.ReadAllText));
            File.WriteAllLines(Path.Combine(MatrixTable.Folder, "ia.md"), lines);
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class MatrixAiTheoryAttribute : TheoryAttribute
{
    public MatrixAiTheoryAttribute() => Skip = MatrixAiFactAttribute.Reason;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class MatrixAiFactAttribute : FactAttribute
{
    public MatrixAiFactAttribute() => Skip = Reason;

    /// <summary>Pruebas largas, FFmpeg, los módulos compilados en <c>modules\</c> y la voz de <c>.entorno\corpus</c>.</summary>
    public static string? Reason => Rodaje.TestMedia.Skip.First(Rodaje.TestMedia.Skip.LongRun, Rodaje.TestMedia.Skip.FfmpegLibraries,
        AudioAiCorpus.Ready ? null : "Faltan los módulos compilados o la voz de prueba (build\\preparar-modelos.ps1 -Corpus).");
}
