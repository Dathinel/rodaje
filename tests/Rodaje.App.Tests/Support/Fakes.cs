using Rodaje.App.Services;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Probe;

namespace Rodaje.App.Tests.Support;

/// <summary>Reproductor de mentira: anota lo que recibe y publica la posición que la prueba le pone.</summary>
internal sealed class FakePlayback : IPlaybackEngine
{
    public List<(Project Project, string SequenceId)> Loads { get; } = [];

    public List<Project> Reloads { get; } = [];

    public List<string> Commands { get; } = [];

    public List<Time> Seeks { get; } = [];

    public PlaybackSnapshot Snapshot { get; set; } = PlaybackSnapshot.Empty;

    public void Load(Project project, string sequenceId) => Loads.Add((project, sequenceId));

    public void Reload(Project project) => Reloads.Add(project);

    public void Play() => Commands.Add("play");

    public void Pause() => Commands.Add("pause");

    public void TogglePlayPause() => Commands.Add("toggle");

    public void Seek(Time position)
    {
        Commands.Add("seek");
        Seeks.Add(position);
    }

    public void StepFrames(int frames) => Commands.Add($"step:{frames}");

    public void SetProxyUsage(ProxyUsage usage) => Commands.Add($"proxies:{usage}");
}

/// <summary>Lector de medios de mentira: cada ruta da lo que la prueba registró, o «no se pudo leer».</summary>
internal sealed class FakeProber : IMediaProber
{
    private readonly Dictionary<string, MediaInfo> _media = new(StringComparer.OrdinalIgnoreCase);

    public int Calls;

    public FakeProber Video(string path, double seconds = 10, int width = 1920, int height = 1080, Rational? rate = null, bool audio = true)
    {
        _media[Path.GetFullPath(path)] = MediaSamples.Video(path, seconds, width, height, rate ?? Rational.Fps30, audio);
        return this;
    }

    public FakeProber Audio(string path, double seconds = 30)
    {
        _media[Path.GetFullPath(path)] = MediaSamples.Audio(path, seconds);
        return this;
    }

    public MediaProbeResult Probe(string path)
    {
        Interlocked.Increment(ref Calls);
        return _media.TryGetValue(path, out MediaInfo? info)
            ? new MediaProbeResult(path, info, MediaSupport.Problem(info))
            : new MediaProbeResult(path, null, "no se pudo leer (archivo de prueba desconocido)");
    }
}

/// <summary>Diálogos de mentira con respuestas fijas; cuenta cuántas veces se preguntó.</summary>
internal sealed class FakeDialogs : IEditorDialogs
{
    public UnsavedChangesChoice Choice { get; set; } = UnsavedChangesChoice.Cancel;

    public string? SavePath { get; set; }

    public string? OpenPath { get; set; }

    public IReadOnlyList<string> MediaFiles { get; set; } = [];

    public int Asked { get; private set; }

    public int SavePicks { get; private set; }

    public Task<UnsavedChangesChoice> AskSaveChangesAsync(string projectName)
    {
        Asked++;
        return Task.FromResult(Choice);
    }

    public Task<string?> PickProjectToOpenAsync() => Task.FromResult(OpenPath);

    public Task<string?> PickProjectSavePathAsync(string suggestedFileName)
    {
        SavePicks++;
        return Task.FromResult(SavePath);
    }

    public Task<IReadOnlyList<string>> PickMediaFilesAsync() => Task.FromResult(MediaFiles);
}

internal static class MediaSamples
{
    public static MediaInfo Video(string path, double seconds, int width, int height, Rational rate, bool audio)
    {
        Time duration = Time.FromSeconds(seconds);
        return new MediaInfo
        {
            Path = Path.GetFullPath(path),
            ContainerFormat = "mov,mp4,m4a,3gp,3g2,mj2",
            Duration = duration,
            ContainerStartTime = Time.Zero,
            Video = new VideoStreamInfo
            {
                StreamIndex = 0,
                Codec = "h264",
                PixelFormat = "yuv420p",
                BitDepth = 8,
                Width = width,
                Height = height,
                SampleAspectRatio = new Rational(1, 1),
                Rotation = 0,
                DisplayWidth = width,
                DisplayHeight = height,
                FrameRate = rate,
                IsVariableFrameRate = false,
                FrameCount = (long)(seconds * rate.Num / rate.Den),
                Duration = duration,
                StartTime = Time.Zero,
            },
            Audio = audio ? AudioStream(duration) : null,
        };
    }

    public static MediaInfo Audio(string path, double seconds)
    {
        Time duration = Time.FromSeconds(seconds);
        return new MediaInfo
        {
            Path = Path.GetFullPath(path),
            ContainerFormat = "mp3",
            Duration = duration,
            ContainerStartTime = Time.Zero,
            Audio = AudioStream(duration),
        };
    }

    private static AudioStreamInfo AudioStream(Time duration) => new()
    {
        StreamIndex = 1,
        Codec = "aac",
        SampleRate = 48000,
        Channels = 2,
        Duration = duration,
        StartTime = Time.Zero,
    };
}
