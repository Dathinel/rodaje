using Rodaje.Analysis;
using Rodaje.Analysis.Ducking;
using Rodaje.App.Services.Modules;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Analysis;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Modules;

/// <summary>«Reducir ruido» y el detector de voz con módulo, del lado de la app.</summary>
public sealed class AudioAiTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Un video de 60 s con su audio enlazado: el clip usa de 10 a 30 s y tiene keyframes de volumen en 12 y 20 s.</summary>
    private static Project Talk()
    {
        var media = new MediaRef { Id = "m", Path = @"C:\videos\entrevista.mp4", Name = "entrevista.mp4", Duration = S(60), HasVideo = true, HasAudio = true, SampleRate = 48_000, AudioChannels = 2, Width = 1920, Height = 1080, FrameRate = Rational.Fps30 };
        var gain = new Parameter { Value = 0, Keyframes = [new Keyframe(S(12), -6), new Keyframe(S(20), 0)] };
        Clip video = new() { Id = "v", MediaId = "m", SourceIn = S(10), SourceOut = S(30), Start = S(5), LinkId = "L" };
        Clip audio = new()
        {
            Id = "a", MediaId = "m", SourceIn = S(10), SourceOut = S(30), Start = S(5), LinkId = "L",
            Effects = [new EffectInstance { Id = "e", Type = BuiltInEffects.Audio, Parameters = { [BuiltInEffects.AudioParameters.GainDb] = gain } }],
        };
        var sequence = new Sequence
        {
            Id = "s", Name = "s",
            VideoTracks = [new Track { Id = "v1", Kind = TrackKind.Video, Name = "V1", Clips = [video] }],
            AudioTracks = [new Track { Id = "a1", Kind = TrackKind.Audio, Name = "A1", Clips = [audio] }],
        };
        return new Project { Id = "p", Name = "p", Media = [media], Sequences = [sequence] };
    }

    [Fact]
    public void The_target_is_the_audio_clip_even_when_the_video_is_selected()
    {
        Sequence sequence = Talk().Sequences[0];
        Assert.Equal("a", NoiseReduction.TargetClip(sequence, ["v"], _ => true)?.Id);
        Assert.Equal("a", NoiseReduction.TargetClip(sequence, ["a"], _ => true)?.Id);
        Assert.Null(NoiseReduction.TargetClip(sequence, ["v"], _ => false));
        sequence.AudioTracks[0].Clips[0].LinkId = null;
        Assert.Null(NoiseReduction.TargetClip(sequence, ["v"], _ => true));
    }

    [Fact]
    public void The_window_adds_handles_inside_the_media()
    {
        Clip clip = Talk().Sequences[0].AudioTracks[0].Clips[0];
        Assert.Equal((S(8), S(32)), NoiseReduction.Window(clip, S(60)));
        clip.SourceIn = S(1);
        clip.SourceOut = S(59);
        Assert.Equal((S(0), S(60)), NoiseReduction.Window(clip, S(60)));
    }

    [Fact]
    public void Applying_moves_the_clip_to_the_clean_media_in_one_undo_step()
    {
        Project project = Talk();
        string before = ProjectSerializer.Serialize(project);
        var undo = new UndoStack(project);
        var clean = new CleanAudio(@"C:\proyecto.medios\ruido.wav", @"C:\videos\entrevista.mp4", S(8), S(24), 24 * 48_000);
        MediaRef media = NoiseReduction.Media("limpio", clean, "entrevista.mp4 (sin ruido)");
        undo.Execute(NoiseReduction.Command("s", "a", media, clean.Offset));

        Clip audio = project.Sequences[0].AudioTracks[0].Clips[0];
        Clip video = project.Sequences[0].VideoTracks[0].Clips[0];
        Assert.Equal("limpio", audio.MediaId);
        Assert.Equal((S(2), S(22), S(5)), (audio.SourceIn, audio.SourceOut, audio.Start));
        Assert.Equal([S(4), S(12)], audio.Effects[0].Parameters[BuiltInEffects.AudioParameters.GainDb].Keyframes.Select(k => k.Time));
        Assert.Equal(("m", S(10)), (video.MediaId, video.SourceIn));
        Assert.Equal("L", audio.LinkId);
        Assert.Contains(project.Media, m => m.Id == "limpio" && m.Duration == S(24));

        Assert.True(undo.Undo());
        Assert.Equal(before, ProjectSerializer.Serialize(project));
        Assert.Equal(0, undo.Count);
    }

    /// <summary>Matriz de formatos: un AVI con MP3 de 12,042449 s, limpiado entero, se rechazaba por una fracción de muestra.</summary>
    [Fact]
    public void A_clip_that_reaches_a_media_end_between_samples_fits_the_clean_audio()
    {
        Project project = Talk();
        Time end = Time.FromSeconds(12.042449);
        project.Media[0].Duration = end;
        Clip audio = project.Sequences[0].AudioTracks[0].Clips[0];
        (audio.SourceIn, audio.SourceOut) = (S(1), end);
        audio.Effects.Clear();
        (Time start, Time stop) = NoiseReduction.Window(audio, end);
        long frames = stop.ToSamples(48_000) - start.ToSamples(48_000);
        var clean = new CleanAudio("ruido.wav", "x.avi", start, stop - start, frames);
        var undo = new UndoStack(project);

        undo.Execute(NoiseReduction.Command("s", "a", NoiseReduction.Media("limpio", clean, "x"), clean.Offset));

        Assert.Equal(("limpio", end), (audio.MediaId, audio.SourceOut));
    }

    [Fact]
    public void A_clip_trimmed_past_the_clean_audio_is_refused()
    {
        Project project = Talk();
        var undo = new UndoStack(project);
        var clean = new CleanAudio("ruido.wav", "x.mp4", S(12), S(10), 10 * 48_000);
        Assert.Throws<Rodaje.Core.Editing.EditException>(() => undo.Execute(NoiseReduction.Command("s", "a", NoiseReduction.Media("limpio", clean, "x"), clean.Offset)));
        Assert.DoesNotContain(project.Media, m => m.Id == "limpio");
    }

    [Fact]
    public void Wav_round_trips_and_stays_whole_after_a_failure()
    {
        string path = Path.Combine(_folder.Path, "a.wav");
        float[] samples = [.. Enumerable.Range(0, 48_000 * 2).Select(i => MathF.Sin(i * 0.01f))];
        NoiseReduction.Wav.Write(path, 2, 48_000, 48_000, (at, count) => samples.AsSpan((int)at, count), blockSamples: 1000);
        Assert.Equal(samples.Length * 4 + NoiseReduction.Wav.HeaderBytes, new FileInfo(path).Length);
        Assert.Equal(samples.AsSpan(200, 400).ToArray(), NoiseReduction.Wav.Read(path, 2, 100, 200));

        Assert.Throws<InvalidOperationException>(() => NoiseReduction.Wav.Write(path, 2, 48_000, 48_000, (_, _) => throw new InvalidOperationException()));
        Assert.Equal(samples.Length * 4 + NoiseReduction.Wav.HeaderBytes, new FileInfo(path).Length);
        Assert.False(File.Exists(path + ".part"));
    }

    [FfmpegFact]
    public void Intensity_mixes_the_original_back_in()
    {
        const int Frames = 48_000;
        string original = Path.Combine(_folder.Path, "original.wav");
        float[] o = [.. Enumerable.Range(0, Frames * 2).Select(i => 0.5f * MathF.Sin(i * 0.003f))];
        float[] c = [.. Enumerable.Range(0, Frames * 2).Select(i => 0.2f * MathF.Cos(i * 0.007f))];
        NoiseReduction.Wav.Write(original, 2, 48_000, Frames, (at, count) => o.AsSpan((int)at, count));
        string cleanPath = Path.Combine(_folder.Path, "limpio.wav");
        NoiseReduction.Wav.Write(cleanPath, 2, 48_000, Frames, (at, count) => c.AsSpan((int)at, count));
        var clean = new CleanAudio(cleanPath, original, Time.Zero, S(1), Frames);

        Assert.Equal(cleanPath, NoiseReduction.Final(clean, NoiseReduction.MaxReductionDb));
        string mixed = NoiseReduction.Final(clean, 12);
        float lim = MathF.Pow(10, -12 / 20f);
        float[] m = NoiseReduction.Wav.Read(mixed, 2, 0, Frames);
        double worst = Enumerable.Range(0, m.Length).Max(i => Math.Abs(m[i] - ((lim * o[i]) + ((1 - lim) * c[i]))));
        Assert.True(worst < 1e-4, $"error {worst}");
    }

    [Fact]
    public async Task Without_the_module_the_energy_detector_answers()
    {
        await using ModuleManager manager = new(new ModuleManagerOptions());
        var detector = new ModuleVoiceDetector(manager, new EnergyVoiceDetector());
        Assert.False(detector.UsesModule);
        IVoiceActivitySession session = detector.Begin(48_000, 2);
        session.Push(Speech(4, 1, 3));
        TimeRange r = Assert.Single(await session.FinishAsync());
        Assert.InRange(r.Start, 0.9, 1.1);
    }

    [Fact]
    public async Task A_failing_module_falls_back_to_energy_without_losing_the_analysis()
    {
        // El eco dice ofrecer «detectar-voz» pero no la tiene: la llamada falla con unknown-function.
        using var modules = new EchoModules();
        modules.Add("falso");
        string manifest = Path.Combine(modules.Root, "falso", "paquete.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"fallar\"]", "\"fallar\", \"detectar-voz\"]", StringComparison.Ordinal));
        await using ModuleManager manager = modules.Manager();
        var failures = new List<ModuleFailure>();
        manager.Failed += (_, f) => failures.Add(f);
        var detector = new ModuleVoiceDetector(manager, new EnergyVoiceDetector());
        Assert.True(detector.UsesModule);
        IVoiceActivitySession session = detector.Begin(48_000, 2);
        session.Push(Speech(4, 1, 3));
        TimeRange r = Assert.Single(await session.FinishAsync());
        Assert.InRange(r.Start, 0.9, 1.1);
        Assert.Single(failures);
    }

    [AudioAiFact]
    public async Task Silero_finds_real_speech_through_the_module_and_cleaning_writes_an_aligned_wav()
    {
        (float[] voice, _) = AudioAiCorpus.ReadMono(AudioAiCorpus.Voice!);
        float[] clip = voice.AsSpan(20 * 48_000, 12 * 48_000).ToArray();
        var stereo = new float[(clip.Length + (48_000 * 3)) * 2];
        for (int i = 0; i < clip.Length; i++)
        {
            stereo[((i + (48_000 * 3)) * 2)] = stereo[((i + (48_000 * 3)) * 2) + 1] = clip[i];
        }

        await using ModuleManager manager = new(new ModuleManagerOptions { Roots = [new ModuleRoot(AudioAiCorpus.ModulesFolder!, ModuleOrigin.Development)] });
        manager.Refresh();
        var failures = new List<ModuleFailure>();
        manager.Failed += (_, f) => failures.Add(f);
        var detector = new ModuleVoiceDetector(manager, new EnergyVoiceDetector());
        Assert.True(detector.UsesModule, string.Join(" ", manager.Problems));
        IVoiceActivitySession session = detector.Begin(48_000, 2);
        session.Push(stereo);
        IReadOnlyList<TimeRange> ranges = await session.FinishAsync();
        Assert.Empty(failures.Select(f => f.Message));
        Assert.NotEmpty(ranges);
        // La voz va de 3 a 15 s (antes, silencio digital): nada antes y la mayor parte después.
        Assert.True(ranges.All(r => r.Start >= 2.9 && r.End <= 15.1) && ranges.Sum(r => r.Duration) > 5, string.Join(", ", ranges));

        string original = Path.Combine(_folder.Path, "voz.wav");
        NoiseReduction.Wav.Write(original, 2, 48_000, stereo.Length / 2, (at, count) => stereo.AsSpan((int)at, count));
        ModuleDescriptor module = manager.FindByFunction(NoiseReduction.Function)!;
        CleanAudio clean = await NoiseReduction.RunAsync(manager, module, original, (S(2), S(10)), _folder.Path, "Reducir ruido", CancellationToken.None);
        Assert.Equal(8 * 48_000, clean.Frames);
        float[] cleaned = NoiseReduction.Wav.Read(clean.Path, 2, 0, (int)clean.Frames);
        float[] source = stereo.AsSpan(2 * 2 * 48_000, cleaned.Length).ToArray();
        double dot = 0, energy = 0;
        for (int i = 0; i < source.Length; i++)
        {
            dot += source[i] * (double)cleaned[i];
            energy += source[i] * (double)source[i];
        }

        Assert.True(dot / energy > 0.7, $"La voz limpia no está alineada con la original (correlación {dot / energy:0.00}).");
    }

    /// <summary>Estéreo de 48 kHz con una voz sintética entre <paramref name="from"/> y <paramref name="to"/> segundos.</summary>
    private static float[] Speech(double seconds, double from, double to)
    {
        var x = new float[(int)(seconds * 48_000) * 2];
        for (int i = (int)(from * 48_000); i < (int)(to * 48_000); i++)
        {
            double t = i / 48_000.0;
            float v = (float)(0.3 * (Math.Sin(2 * Math.PI * 180 * t) + (0.5 * Math.Sin(2 * Math.PI * 900 * t)) + (0.3 * Math.Sin(2 * Math.PI * 2100 * t))));
            x[2 * i] = x[(2 * i) + 1] = v;
        }

        return x;
    }
}

/// <summary>Lo que necesita la prueba de punta a punta: el módulo compilado en <c>modules\</c>, sus modelos y voz real en <c>.entorno</c>.</summary>
internal static class AudioAiCorpus
{
    public static string? ModulesFolder { get; } = ModuleCatalog.FindDevelopmentModules(AppContext.BaseDirectory);

    public static string? Voice { get; } = Find(Path.Combine("corpus", "audio-ia", "voz-48k.wav"));

    public static bool Ready => ModulesFolder is not null && Voice is not null && Find(Path.Combine("modelos", "silero_vad.onnx")) is not null
        && Directory.Exists(Path.Combine(ModulesFolder, "Rodaje.Module.AudioAi", "bin"));

    public static (float[] Samples, int Rate) ReadMono(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int data = bytes.AsSpan().IndexOf("data"u8);
        int count = BitConverter.ToInt32(bytes, data + 4) / 4;
        return (System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes.AsSpan(data + 8, count * 4)).ToArray(), BitConverter.ToInt32(bytes, 24));
    }

    private static string? Find(string relative)
    {
        string? environment = Environment.GetEnvironmentVariable("RODAJE_ENTORNO");
        if (environment is { Length: > 0 } && File.Exists(Path.Combine(environment, relative)))
        {
            return Path.Combine(environment, relative);
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".entorno", relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class AudioAiFactAttribute : FactAttribute
{
    public AudioAiFactAttribute() =>
        Skip = Rodaje.TestMedia.Skip.FfmpegLibraries
            ?? (AudioAiCorpus.Ready ? null : "Falta modulo-audio-ia compilado, sus modelos o la voz de prueba (build\\preparar-modelos.ps1 -Corpus).");
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute() => Skip = Rodaje.TestMedia.Skip.FfmpegLibraries;
}
