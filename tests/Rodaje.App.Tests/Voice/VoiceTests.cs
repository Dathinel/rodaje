using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using Rodaje.App.Services;
using Rodaje.App.Services.Voice;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Tests.Voice;

/// <summary>Texto a voz: ajustes y su JSON, diálogo con el módulo (con un módulo falso en memoria compartida real), archivos e inserción.</summary>
public class VoiceTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static readonly VoiceSettings Hola = new("Hola, ¿qué tal? [pausa 1.5] Adiós.", "es-419", "em_alex", 1.2, -2, 0.5, 0.2);

    [Fact]
    public void ElJsonDeAlLadoVaYVuelveYSeReconoce()
    {
        string json = Hola.ToSidecar();
        Assert.Contains("\"generator\": \"modulo-voz\"", json, StringComparison.Ordinal);
        Assert.Contains("¿qué tal?", json, StringComparison.Ordinal);
        Assert.Equal(Hola, VoiceSettings.FromSidecar(json));
        Assert.Null(VoiceSettings.FromSidecar("{\"generator\":\"otro\",\"text\":\"x\"}"));
        Assert.Null(VoiceSettings.FromSidecar("{roto"));
        Assert.Null(VoiceSettings.FromSidecar("[]"));
    }

    [Fact]
    public void LosValoresFueraDeRangoSeAjustanYLosParametrosLlevanTodo()
    {
        VoiceSettings wild = new("x", Speed: 9, Pitch: -40, SentencePause: double.NaN, CommaPause: -1);
        Assert.Equal(new VoiceSettings("x", Speed: 2, Pitch: -6, SentencePause: 0.4, CommaPause: 0), wild.Clamped());

        JsonElement p = Hola.ToParameters();
        Assert.Equal(Hola.Text, p.GetProperty("text").GetString());
        Assert.Equal("es-419", p.GetProperty("language").GetString());
        Assert.Equal("em_alex", p.GetProperty("voice").GetString());
        Assert.Equal(1.2, p.GetProperty("speed").GetDouble());
        Assert.Equal(-2, p.GetProperty("pitch").GetDouble());
        Assert.Equal(0.5, p.GetProperty("sentencePause").GetDouble());
        Assert.Equal(0.2, p.GetProperty("commaPause").GetDouble());
        Assert.False(p.TryGetProperty("generator", out _));
    }

    [Fact]
    public void SoloCambiaSiCambiaLoQueSeOye()
    {
        Assert.False(Hola.SoundsDifferent(Hola with { Text = Hola.Text + "  " }));
        Assert.True(Hola.SoundsDifferent(Hola with { Voice = "ef_dora" }));
        Assert.True(Hola.SoundsDifferent(Hola with { Text = "Hola." }));
        Assert.True(Hola.SoundsDifferent(Hola with { Pitch = 0 }));
    }

    [Fact]
    public void LaEstimacionCuentaPausasYVelocidad()
    {
        double normal = new VoiceSettings(new string('a', 110)).EstimateSeconds();
        double fast = new VoiceSettings(new string('a', 110), Speed: 2).EstimateSeconds();
        Assert.InRange(normal, 11.9, 12.1);
        Assert.True(fast < normal);
        double paused = new VoiceSettings("Hola [pausa 3] chau [pausa]").EstimateSeconds();
        Assert.True(paused > 2 + 3.5);
    }

    [Fact]
    public void ElCatalogoSaltaLoQueNoEntiende()
    {
        using JsonDocument doc = JsonDocument.Parse("""
            { "languages": [ {"id":"es","name":"Español (España)"}, {"id":3}, "x" ],
              "voices": [ {"id":"ef_dora","name":"Dora","languages":["es","es-419"],"gender":"f","engine":"kokoro","license":"Apache-2.0"},
                          {"name":"sin id"}, {"id":"af_heart","languages":["en-US", 4]} ] }
            """);
        VoiceCatalog c = VoiceCatalog.Parse(doc.RootElement);
        Assert.Equal(["es"], c.Languages.Select(l => l.Id));
        Assert.Equal(["ef_dora", "af_heart"], c.Voices.Select(v => v.Id));
        Assert.Equal("af_heart", c.Voices[1].Name);
        Assert.Equal(["ef_dora"], c.VoicesFor("es-419").Select(v => v.Id));
        Assert.Same(VoiceCatalog.Empty, VoiceCatalog.Parse(null));
    }

    [Fact]
    public async Task SiElAudioCabeSeLeeDelBufer()
    {
        using var call = new FakeVoiceCall();
        call.Handler = (fn, p, buffers) =>
        {
            Assert.Equal("voz", fn);
            Assert.Equal("em_alex", p!.Value.GetProperty("voice").GetString());
            SharedBufferRef output = Assert.Single(buffers!);
            Assert.Equal(24_000, output.Audio!.SampleRate);
            Assert.True(output.Audio.Frames >= 24_000 * 5, "reserva para el texto y la pausa");
            Span<float> f = MemoryMarshal.Cast<byte, float>(call.Map(output));
            for (int i = 0; i < 1000; i++)
            {
                f[i] = i == 7 ? float.NaN : i == 8 ? 5f : 0.25f;
            }

            return Result("""{"sampleRate":24000,"frames":1000,"written":true,"segments":[{"start":0,"end":0.04,"text":"Hola"}],"words":[{"start":0,"end":0.02,"text":"Hola"},{"start":-1,"end":0,"text":"mal"}]}""",
                output with { Length = 4000, Audio = output.Audio with { Frames = 1000 } });
        };

        SynthesizedVoice v = await VoiceSynthesis.SynthesizeAsync(call, Hola);

        Assert.Equal(1000, v.Samples.Length);
        Assert.Equal(0.25f, v.Samples[0]);
        Assert.Equal(0f, v.Samples[7]);
        Assert.Equal(1f, v.Samples[8]);
        Assert.Equal(["Hola"], v.Words.Select(w => w.Text));
        Assert.Single(v.Segments);
        Assert.Equal(["voz"], call.Calls);
    }

    [Fact]
    public async Task SiNoCabeSeTraeConEntregarEnUnBuferExacto()
    {
        using var call = new FakeVoiceCall();
        call.Handler = (fn, p, buffers) =>
        {
            if (fn == "voz")
            {
                return Result("""{"sampleRate":24000,"frames":50000,"written":false,"audio":"t-1"}""");
            }

            Assert.Equal("entregar", fn);
            Assert.Equal("t-1", p!.Value.GetProperty("audio").GetString());
            SharedBufferRef exact = Assert.Single(buffers!);
            Assert.Equal(200_000, exact.Length);
            MemoryMarshal.Cast<byte, float>(call.Map(exact)).Fill(-0.5f);
            return Result("{}", exact);
        };

        SynthesizedVoice v = await VoiceSynthesis.SynthesizeAsync(call, new VoiceSettings("Corto."));

        Assert.Equal(50_000, v.Samples.Length);
        Assert.All(v.Samples, s => Assert.Equal(-0.5f, s));
        Assert.Equal(["voz", "entregar"], call.Calls);
    }

    [Theory]
    [InlineData("""{"sampleRate":24000,"frames":0,"written":true}""")]
    [InlineData("""{"sampleRate":3,"frames":10,"written":true}""")]
    [InlineData("""{"sampleRate":24000,"frames":10,"written":false}""")]
    [InlineData("""{"sampleRate":24000,"frames":999999999,"written":true}""")]
    [InlineData("""[]""")]
    public async Task UnaRespuestaInvalidaFallaConVoiceException(string json)
    {
        using var call = new FakeVoiceCall { Handler = (_, _, _) => Result(json) };
        await Assert.ThrowsAsync<VoiceException>(() => VoiceSynthesis.SynthesizeAsync(call, new VoiceSettings("Hola.")));
    }

    [Fact]
    public async Task SinTextoNoLlamaAlModulo()
    {
        using var call = new FakeVoiceCall();
        await Assert.ThrowsAsync<VoiceException>(() => VoiceSynthesis.SynthesizeAsync(call, new VoiceSettings("   ")));
        Assert.Empty(call.Calls);
    }

    [Fact]
    public void ElWavYSuJsonQuedanJuntoAlProyecto()
    {
        using var dir = new TempDirectory();
        string folder = GeneratedMedia.Folder(dir.File("mi proyecto.rodaje"), null);
        Assert.Equal(Path.Combine(dir.Path, "mi proyecto.medios"), folder);

        var voice = new SynthesizedVoice([0.5f, -0.5f, 0.25f], 24_000, [], []);
        string wav = VoiceFiles.Save(folder, Hola, voice);

        Assert.StartsWith("Voz - Hola qué tal pausa 1 5 Adió", Path.GetFileName(wav), StringComparison.Ordinal);
        byte[] bytes = File.ReadAllBytes(wav);
        Assert.Equal(44 + 12, bytes.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(3, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(20)));
        Assert.Equal(24_000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(-0.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(48)));
        Assert.Equal(Hola, VoiceFiles.ReadSettings(wav));
        Assert.Null(VoiceFiles.ReadSettings(dir.File("otro.wav")));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    [Fact]
    public void LaVistaPreviaPasaA48kEstereo()
    {
        float[] s = VoicePreviewPlayer.ToStereo48k([0f, 1f], 24_000);
        Assert.Equal([0f, 0f, 0.5f, 0.5f, 1f, 1f, 1f, 1f], s);
        Assert.Empty(VoicePreviewPlayer.ToStereo48k([], 24_000));
    }

    [Fact]
    public void InsertarPoneLaVozEnElCursorEnUnPasoYSeDeshace() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        EditorSession session = await OneClip();
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = S(2) });
        int steps = session.History.Count;
        int media = session.Project.Media.Count;
        string wav = VoiceFiles.Save(dir.Path, Hola, new SynthesizedVoice(new float[48_000], 24_000, [], []));

        Assert.True(session.InsertGeneratedVoice(wav, Hola, 48_000, 24_000));

        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal(media + 1, session.Project.Media.Count);
        string id = Assert.Single(session.SelectedClipIds);
        (Track track, Clip clip) = session.Sequence.FindClip(id)!.Value;
        Assert.Equal(TrackKind.Audio, track.Kind);
        Assert.Equal((S(2), S(4)), (clip.Start, clip.End));
        Assert.DoesNotContain(track.Clips, c => c != clip && c.Start < clip.End && c.End > clip.Start);
        Assert.StartsWith("Voz: Hola", session.Project.FindMedia(clip.MediaId)!.Name, StringComparison.Ordinal);
        Assert.Equal(Hola, session.SelectedGeneratedVoice()!.Value.Settings);

        Assert.True(session.Undo());
        Assert.Equal(media, session.Project.Media.Count);
        Assert.Null(session.Sequence.FindClip(id));
    });

    [Fact]
    public void SiLaUnicaPistaDeAudioEstaOcupadaSeCreaLaPistaVoz() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        EditorSession session = await OneClip(new NewProjectTracks(1, 1));
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = S(3) });
        int tracks = session.Sequence.AudioTracks.Count;
        string wav = VoiceFiles.Save(dir.Path, Hola, new SynthesizedVoice(new float[24_000], 24_000, [], []));

        Assert.True(session.InsertGeneratedVoice(wav, Hola, 24_000, 24_000));

        Assert.Equal(tracks + 1, session.Sequence.AudioTracks.Count);
        Assert.Equal("Voz", session.Sequence.AudioTracks[^1].Name);
        Assert.True(session.Undo());
        Assert.Equal(tracks, session.Sequence.AudioTracks.Count);
    });

    [Fact]
    public void RegenerarCambiaElAudioEnUnPasoYQuitaElMedioViejo() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        EditorSession session = await OneClip();
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = S(1) });
        string first = VoiceFiles.Save(dir.Path, Hola, new SynthesizedVoice(new float[24_000], 24_000, [], []));
        Assert.True(session.InsertGeneratedVoice(first, Hola, 24_000, 24_000));
        string id = session.SelectedClipIds[0];
        string oldMedia = session.Sequence.FindClip(id)!.Value.Clip.MediaId;
        int steps = session.History.Count;

        VoiceSettings changed = Hola with { Voice = "ef_dora" };
        string second = VoiceFiles.Save(dir.Path, changed, new SynthesizedVoice(new float[36_000], 24_000, [], []));
        Assert.True(session.ReplaceGeneratedVoice(id, second, changed, 36_000, 24_000));

        Assert.Equal(steps + 1, session.History.Count);
        Clip clip = session.Sequence.FindClip(id)!.Value.Clip;
        Assert.Equal((S(1), S(2.5)), (clip.Start, clip.End));
        Assert.Null(session.Project.FindMedia(oldMedia));
        Assert.Equal("ef_dora", session.SelectedGeneratedVoice()!.Value.Settings.Voice);

        Assert.True(session.Undo());
        Assert.Equal(oldMedia, session.Sequence.FindClip(id)!.Value.Clip.MediaId);
        Assert.NotNull(session.Project.FindMedia(oldMedia));
    });

    [Fact]
    public void RegenerarNoPisaElClipSiguiente() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        EditorSession session = await OneClip(new NewProjectTracks(1, 1));
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = S(12) });
        string a = VoiceFiles.Save(dir.Path, Hola, new SynthesizedVoice(new float[24_000], 24_000, [], []));
        Assert.True(session.InsertGeneratedVoice(a, Hola, 24_000, 24_000));
        string first = session.SelectedClipIds[0];
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = S(13) });
        Assert.True(session.InsertGeneratedVoice(a, Hola, 24_000, 24_000));
        Assert.Equal(session.Sequence.FindClip(first)!.Value.Track, session.Sequence.FindClip(session.SelectedClipIds[0])!.Value.Track);
        int steps = session.History.Count;

        Assert.False(session.ReplaceGeneratedVoice(first, a, Hola, 48_000, 24_000));

        Assert.Equal(steps, session.History.Count);
        Assert.Equal(S(13), session.Sequence.FindClip(first)!.Value.Clip.End);
    });

    [Fact]
    public void ElTextoSeTomaDelClipDeTextoSeleccionado() => UiThread.Run(async () =>
    {
        EditorSession session = await OneClip();
        Assert.Null(session.SelectedClipText());
        Assert.True(session.AddTitleAtPlayhead());
        Assert.Equal(session.Sequence.FindClip(session.SelectedClipIds[0])!.Value.Clip.Generator!.Text, session.SelectedClipText());
        Assert.Null(session.SelectedGeneratedVoice());
    });

    private static async Task<EditorSession> OneClip(NewProjectTracks? tracks = null)
    {
        var prober = new FakeProber().Video("a.mp4", 10);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator(), tracks);
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        return session;
    }

    private static ModuleResult Result(string json, params SharedBufferRef[] buffers)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return new ModuleResult(doc.RootElement.Clone(), buffers);
    }

    /// <summary>Una llamada de módulo en este proceso: regiones de memoria compartida de verdad y un manejador por función.</summary>
    private sealed class FakeVoiceCall : IVoiceModuleCall, IDisposable
    {
        private readonly List<SharedMemoryRegion> _regions = [];

        public Func<string, JsonElement?, IReadOnlyList<SharedBufferRef>?, ModuleResult> Handler { get; set; } = (_, _, _) => throw new InvalidOperationException();

        public List<string> Calls { get; } = [];

        public SharedBufferRef CreateAudioBuffer(long frames, int sampleRate)
        {
            SharedMemoryRegion region = SharedMemoryRegion.Create(frames * 4);
            _regions.Add(region);
            return region.Describe(0, frames * 4, audio: new AudioLayout(sampleRate, 1, frames));
        }

        public Span<byte> Map(SharedBufferRef buffer) => _regions.Single(r => r.Name == buffer.Region).GetSpan(buffer);

        public Task<ModuleResult> InvokeAsync(string functionId, JsonElement? parameters, IReadOnlyList<SharedBufferRef>? buffers, double progressFrom, double progressTo)
        {
            Calls.Add(functionId);
            return Task.FromResult(Handler(functionId, parameters, buffers));
        }

        public void Dispose()
        {
            foreach (SharedMemoryRegion r in _regions)
            {
                r.Dispose();
            }
        }
    }
}
