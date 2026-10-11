using System.Collections.Concurrent;
using Rodaje.App.Services.Media;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Timing;
using Rodaje.Media;
using Rodaje.Media.Video;

namespace Rodaje.App.Tests.Media;

/// <summary>Decodificadores falsos: cada miniatura lleva en su primer píxel el instante en décimas de segundo.</summary>
internal sealed class FakeSources : IVisualsSources
{
    public ConcurrentQueue<(string Path, long Ticks, int Height)> Decoded { get; } = [];

    public ConcurrentDictionary<string, int> Opened { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Broken { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Archivos que abren pero no dan ningún cuadro.</summary>
    public HashSet<string> Empty { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Archivos cuyo decodificador falla con una excepción que nadie espera.</summary>
    public HashSet<string> Faulty { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mientras esté cerrada, decodificar se queda colgado sin mirar la cancelación.</summary>
    public ManualResetEventSlim Stuck { get; } = new(true);

    public int Attempts => Volatile.Read(ref _attempts);

    private int _attempts;

    public int AudioSeconds { get; set; } = 3;

    public ManualResetEventSlim Gate { get; } = new(true);

    public IThumbnailReader OpenThumbnails(string path)
    {
        Opened.AddOrUpdate(path, 1, (_, n) => n + 1);
        return Broken.Contains(path) ? throw new MediaException($"No se pudo abrir {path}.") : new Reader(this, path);
    }

    public IAudioSampleReader? OpenAudio(string path) => path.EndsWith(".mudo", StringComparison.Ordinal) ? null
        : Faulty.Contains(path) ? throw new ArgumentOutOfRangeException(nameof(path), "Fallo inesperado.") : new Audio(AudioSeconds);

    public string Fingerprint(string path) => "f" + Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(path)).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class Reader(FakeSources owner, string path) : IThumbnailReader
    {
        public BgraImage? Decode(Time sourceTime, int height, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner._attempts);
            owner.Gate.Wait(cancellationToken);
            owner.Stuck.Wait(CancellationToken.None);
            if (owner.Faulty.Contains(path))
            {
                throw new ArgumentOutOfRangeException(nameof(sourceTime), "Fallo inesperado.");
            }

            if (owner.Empty.Contains(path))
            {
                return null;
            }

            owner.Decoded.Enqueue((path, sourceTime.Ticks, height));
            int width = height * 16 / 9;
            byte[] pixels = new byte[width * height * 4];
            pixels[0] = (byte)Math.Round(sourceTime.ToSeconds() * 10);
            return new BgraImage(width, height, pixels);
        }

        public void Dispose()
        {
        }
    }

    private sealed class Audio(int seconds) : IAudioSampleReader
    {
        private long _left = seconds * 48_000L;

        public int SampleRate => 48_000;

        public int Channels => 2;

        public long ExpectedSamples => seconds * 48_000L;

        public int Read(Span<float> interleaved)
        {
            int frames = (int)Math.Min(_left, interleaved.Length / 2);
            for (int i = 0; i < frames; i++)
            {
                interleaved[2 * i] = 0.5f;
                interleaved[(2 * i) + 1] = -0.25f;
            }

            _left -= frames;
            return frames;
        }

        public void Dispose()
        {
        }
    }
}

public sealed class ClipVisualsTests : IDisposable
{
    private readonly TempDirectory _folder = new();
    private readonly FakeSources _sources = new();
    private readonly Dictionary<string, string> _media = new()
    {
        ["m1"] = @"C:\videos\toma 1.mp4",
        ["m2"] = @"C:\videos\toma 1.mp4",
        ["m3"] = @"C:\videos\roto.mp4",
        ["a1"] = @"C:\audio\música.wav",
        ["a2"] = @"C:\audio\nada.mudo",
        ["v0"] = @"C:\videos\vacío.mp4",
        ["x1"] = @"C:\videos\raro.mp4",
    };

    private readonly BlockingCollection<ClipVisualsChangedEventArgs> _events = [];

    public void Dispose()
    {
        _events.Dispose();
        _folder.Dispose();
        _sources.Gate.Dispose();
        _sources.Stuck.Set();
    }

    private ClipVisuals Create(string? cache = null, int workers = 2)
    {
        var visuals = new ClipVisuals(id => _media.GetValueOrDefault(id), _sources, new ClipVisualsOptions { CacheDirectory = cache, ThumbnailWorkers = workers });
        visuals.Changed += (_, e) => _events.Add(e);
        return visuals;
    }

    private static T WaitFor<T>(Func<T?> probe) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } value)
            {
                return value;
            }

            Thread.Sleep(5);
        }

        throw new TimeoutException("No llegó a tiempo.");
    }

    [Fact]
    public void A_filmstrip_starts_empty_and_fills_in_the_background()
    {
        using ClipVisuals visuals = Create();

        Filmstrip first = visuals.GetFilmstrip("m1", Time.Zero, Time.FromSeconds(10), 5, 40);
        Filmstrip done = WaitFor(() => visuals.GetFilmstrip("m1", Time.Zero, Time.FromSeconds(10), 5, 40) is { IsComplete: true } f ? f : null);

        Assert.False(first.IsComplete);
        Assert.All(first.Frames, Assert.Null);
        Assert.Equal(5, done.Frames.Count);
        Assert.All(done.Frames, f => Assert.Equal(48, f!.Height));
        Assert.Equal(done.Times.Select(t => (byte)Math.Round(t.ToSeconds() * 10)), done.Frames.Select(f => f!.Pixels[0]));
        Assert.Equal([.. done.Times.Select(t => t.Ticks).Order()], _sources.Decoded.Select(d => d.Ticks).Order());
    }

    [Fact]
    public void Changed_names_every_media_that_shares_the_file()
    {
        using ClipVisuals visuals = Create();
        _sources.Gate.Reset();
        visuals.GetThumbnail("m2", Time.FromSeconds(1), 64);
        visuals.GetThumbnail("m1", Time.FromSeconds(1), 64);
        _sources.Gate.Set();

        var ids = new HashSet<string>();
        while (ids.Count < 2 && _events.TryTake(out ClipVisualsChangedEventArgs? e, TimeSpan.FromSeconds(10)))
        {
            Assert.Equal(ClipVisualKind.Thumbnails, e.Kind);
            ids.Add(e.MediaId);
        }

        Assert.Equal(["m1", "m2"], ids.Order());
        Assert.NotNull(visuals.GetThumbnail("m2", Time.FromSeconds(1), 64));
        Assert.Single(_sources.Decoded);
    }

    [Fact]
    public void A_newer_request_goes_first()
    {
        // Con un solo hilo el orden es determinista: la tanda vieja cede el paso antes de su siguiente miniatura.
        using ClipVisuals visuals = Create(workers: 1);
        _sources.Gate.Reset();
        visuals.GetFilmstrip("m1", Time.Zero, Time.FromSeconds(100), 50, 32);
        Thread.Sleep(50);
        BgraImage? urgent = visuals.GetThumbnail("a1", Time.FromSeconds(7), 32);
        _sources.Gate.Set();

        WaitFor(() => visuals.GetThumbnail("a1", Time.FromSeconds(7), 32));
        int position = _sources.Decoded.ToList().FindIndex(d => d.Path == _media["a1"]);
        WaitFor(() => visuals.GetFilmstrip("m1", Time.Zero, Time.FromSeconds(100), 50, 32) is { IsComplete: true } f ? f : null);

        Assert.Null(urgent);
        Assert.InRange(position, 0, 1);
        Assert.Equal(51, _sources.Decoded.Count);
    }

    [Fact]
    public void A_file_that_cannot_be_decoded_is_reported_once_and_not_retried()
    {
        _sources.Broken.Add(_media["m3"]);
        using ClipVisuals visuals = Create();

        visuals.GetFilmstrip("m3", Time.Zero, Time.FromSeconds(5), 4, 32);
        string problem = WaitFor(() => visuals.ThumbnailProblem(_media["m3"]));
        Filmstrip after = visuals.GetFilmstrip("m3", Time.Zero, Time.FromSeconds(5), 4, 32);
        Thread.Sleep(50);

        Assert.Contains("roto.mp4", problem, StringComparison.Ordinal);
        Assert.True(after.IsComplete);
        Assert.Equal(1, _sources.Opened[_media["m3"]]);
    }

    [Fact]
    public void Unknown_media_gives_empty_results()
    {
        using ClipVisuals visuals = Create();

        Assert.Same(Filmstrip.Empty, visuals.GetFilmstrip("nada", Time.Zero, Time.FromSeconds(1), 3, 32));
        Assert.Null(visuals.GetThumbnail("nada", Time.Zero, 32));
        Assert.Same(WaveformSlice.None, visuals.GetWaveform("nada", Time.Zero, Time.FromSeconds(1), 10));
    }

    [Fact]
    public void Waveform_is_computed_once_and_answers_any_range()
    {
        using ClipVisuals visuals = Create();

        WaveformSlice pending = visuals.GetWaveform("a1", Time.Zero, Time.FromSeconds(3), 30);
        WaveformSlice done = WaitFor(() => visuals.GetWaveform("a1", Time.Zero, Time.FromSeconds(3), 30) is { IsComplete: true } w ? w : null);
        WaveformSlice tail = visuals.GetWaveform("a1", Time.FromSeconds(2), Time.FromSeconds(4), 4);

        Assert.False(pending.IsComplete);
        Assert.True(pending.HasAudio);
        Assert.Equal(30, pending.Buckets);
        Assert.All(done.Max, v => Assert.Equal(0.5f, v, 3));
        Assert.All(done.Min, v => Assert.Equal(-0.25f, v, 3));
        Assert.Equal(0.5f, tail.Max[0], 3);
        Assert.Equal(0.5f, tail.Max[1], 3);
        Assert.Equal(0f, tail.Max[3]);
    }

    [Fact]
    public void Media_without_audio_has_no_waveform()
    {
        using ClipVisuals visuals = Create();

        visuals.GetWaveform("a2", Time.Zero, Time.FromSeconds(1), 10);
        WaitFor(() => visuals.GetWaveform("a2", Time.Zero, Time.FromSeconds(1), 10) is { HasAudio: false } w ? w : null);
    }

    [Fact]
    public void A_second_session_reads_thumbnails_and_peaks_from_disk()
    {
        string cache = _folder.File("cache");
        using (ClipVisuals visuals = Create(cache))
        {
            visuals.GetFilmstrip("m1", Time.Zero, Time.FromSeconds(4), 4, 32);
            visuals.GetWaveform("a1", Time.Zero, Time.FromSeconds(3), 10);
            WaitFor(() => visuals.GetFilmstrip("m1", Time.Zero, Time.FromSeconds(4), 4, 32) is { IsComplete: true } f ? f : null);
            WaitFor(() => visuals.GetWaveform("a1", Time.Zero, Time.FromSeconds(3), 10) is { IsComplete: true } w ? w : null);
        }

        int decoded = _sources.Decoded.Count;
        _sources.AudioSeconds = 0;
        using ClipVisuals again = Create(cache);
        Filmstrip strip = WaitFor(() => again.GetFilmstrip("m1", Time.Zero, Time.FromSeconds(4), 4, 32) is { IsComplete: true } f ? f : null);
        WaveformSlice wave = WaitFor(() => again.GetWaveform("a1", Time.Zero, Time.FromSeconds(3), 10) is { IsComplete: true } w ? w : null);

        Assert.Equal(4, decoded);
        Assert.Equal(decoded, _sources.Decoded.Count);
        Assert.Equal(4, again.ThumbnailsFromDisk);
        Assert.All(strip.Frames, Assert.NotNull);
        Assert.All(wave.Max, v => Assert.Equal(0.5f, v, 3));
        Assert.True(Directory.EnumerateFiles(Path.Combine(cache, "miniaturas"), "*.miniatura", SearchOption.AllDirectories).Count() == 4);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(cache, "ondas"), "*.picos"));
    }

    [Fact]
    public void Single_requests_snap_to_the_spacing_grid_and_fall_back_to_the_nearest_ready_thumbnail()
    {
        using ClipVisuals visuals = Create();
        Time spacing = Time.FromSeconds(2);

        Assert.Null(visuals.GetNearestThumbnail("m1", Time.FromSeconds(4.3), spacing, 40));
        BgraImage four = WaitFor(() => visuals.GetNearestThumbnail("m1", Time.FromSeconds(4.3), spacing, 40));
        BgraImage? meanwhile = visuals.GetNearestThumbnail("m1", Time.FromSeconds(8.9), spacing, 40);

        Assert.Equal(40, four.Pixels[0]);
        Assert.Same(four, meanwhile);
        Assert.Equal(48, four.Height);
        BgraImage eight = WaitFor(() => visuals.GetNearestThumbnail("m1", Time.FromSeconds(8.9), spacing, 40) is { } i && i.Pixels[0] == 80 ? i : null);
        Assert.Equal(80, eight.Pixels[0]);
        Assert.Null(visuals.GetNearestThumbnail("nada", Time.Zero, spacing, 40));
    }

    [Fact]
    public void Changed_is_raised_on_the_given_context()
    {
        UiThread.Run(async () =>
        {
            int uiThread = Environment.CurrentManagedThreadId;
            var raisedOn = new TaskCompletionSource<int>();
            using var visuals = new ClipVisuals(id => _media.GetValueOrDefault(id), _sources, null, SynchronizationContext.Current);
            visuals.Changed += (_, _) => raisedOn.TrySetResult(Environment.CurrentManagedThreadId);

            visuals.GetThumbnail("m1", Time.Zero, 32);

            Assert.Equal(uiThread, await raisedOn.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        });
    }

    [Fact]
    public void A_video_without_frames_is_remembered_and_not_decoded_on_every_redraw()
    {
        _sources.Empty.Add(_media["v0"]);
        using ClipVisuals visuals = Create();

        visuals.GetFilmstrip("v0", Time.Zero, Time.FromSeconds(5), 4, 32);
        string problem = WaitFor(() => visuals.ThumbnailProblem(_media["v0"]));
        for (int i = 0; i < 20; i++)
        {
            Assert.True(visuals.GetFilmstrip("v0", Time.Zero, Time.FromSeconds(5), 4, 32).IsComplete);
            visuals.GetThumbnail("v0", Time.FromSeconds(i), 32);
        }

        Thread.Sleep(50);
        Assert.Contains("cuadros", problem, StringComparison.Ordinal);
        Assert.Equal(1, _sources.Attempts);
        Assert.Equal(1, _sources.Opened[_media["v0"]]);
    }

    [Fact]
    public void An_unexpected_exception_stays_with_its_file_and_the_workers_keep_going()
    {
        _sources.Faulty.Add(_media["x1"]);
        using var visuals = new ClipVisuals(id => _media.GetValueOrDefault(id), _sources, new ClipVisualsOptions { ThumbnailWorkers = 1 });

        visuals.GetThumbnail("x1", Time.Zero, 32);
        visuals.GetWaveform("x1", Time.Zero, Time.FromSeconds(1), 10);
        string problem = WaitFor(() => visuals.ThumbnailProblem(_media["x1"]));
        WaitFor(() => visuals.GetWaveform("x1", Time.Zero, Time.FromSeconds(1), 10) is { HasAudio: false } w ? w : null);

        Assert.StartsWith("Fallo inesperado.", problem, StringComparison.Ordinal);
        Assert.NotNull(WaitFor(() => visuals.GetThumbnail("m1", Time.Zero, 32)));
        Assert.True(WaitFor(() => visuals.GetWaveform("a1", Time.Zero, Time.FromSeconds(1), 10) is { IsComplete: true } w ? w : null).HasAudio);
    }

    [Fact]
    public void Removed_media_is_forgotten_and_read_again_if_it_comes_back()
    {
        using ClipVisuals visuals = Create();
        WaitFor(() => visuals.GetThumbnail("m1", Time.Zero, 32));
        WaitFor(() => visuals.GetThumbnail("m2", Time.Zero, 32));
        WaitFor(() => visuals.GetWaveform("a1", Time.Zero, Time.FromSeconds(1), 10) is { IsComplete: true } w ? w : null);
        int decoded = _sources.Decoded.Count;

        _media.Remove("m1");
        visuals.ForgetRemovedMedia();
        BgraImage? shared = visuals.GetThumbnail("m2", Time.Zero, 32);
        _media.Remove("m2");
        _media.Remove("a1");
        visuals.ForgetRemovedMedia();
        _media["m1"] = @"C:\videos\toma 1.mp4";
        _media["a1"] = @"C:\audio\música.wav";
        BgraImage? back = visuals.GetThumbnail("m1", Time.Zero, 32);
        WaveformSlice wave = visuals.GetWaveform("a1", Time.Zero, Time.FromSeconds(1), 10);

        Assert.Equal(1, decoded);
        Assert.NotNull(shared);
        Assert.Null(back);
        Assert.False(wave.IsComplete);
        WaitFor(() => visuals.GetThumbnail("m1", Time.Zero, 32));
        Assert.Equal(2, _sources.Decoded.Count);
    }

    [Fact]
    public void Dispose_does_not_wait_for_a_reader_that_ignores_cancellation()
    {
        var visuals = new ClipVisuals(id => _media.GetValueOrDefault(id), _sources);
        _sources.Stuck.Reset();
        visuals.GetThumbnail("m1", Time.Zero, 32);
        WaitFor(() => _sources.Attempts > 0 ? "" : null);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        visuals.Dispose();
        clock.Stop();
        _sources.Stuck.Set();

        Assert.InRange(clock.ElapsedMilliseconds, 0, 1000);
    }
}
