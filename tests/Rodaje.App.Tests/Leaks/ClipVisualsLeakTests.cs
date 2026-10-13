using Rodaje.App.Services.Media;
using Rodaje.App.Tests.Media;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Leaks;

/// <summary>Fugas de <see cref="ClipVisuals"/> al abrir y cerrar proyectos: lo que se olvida no debe quedar en ninguna cola.</summary>
public sealed class ClipVisualsLeakTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly FakeSources _sources = new() { AudioSeconds = 1 };
    private readonly Dictionary<string, string> _media = new(StringComparer.Ordinal);

    public void Dispose()
    {
        _sources.Gate.Dispose();
        _sources.Stuck.Set();
    }

    [Fact]
    public void Going_back_and_forth_between_projects_keeps_the_waveform_eviction_queue_bounded()
    {
        const int max = 4;
        using var visuals = new ClipVisuals(id => _media.GetValueOrDefault(id), _sources, new ClipVisualsOptions { MaxWaveformsInMemory = max, ThumbnailWorkers = 1 });

        // Como la app: abrir otro proyecto llama a ForgetRemovedMedia y los medios del anterior se olvidan.
        for (int cycle = 0; cycle < 100; cycle++)
        {
            _media.Clear();
            for (int i = 0; i < 3; i++)
            {
                _media[$"a{i}"] = $@"C:\proyecto {cycle % 2}\audio {i}.wav";
            }

            visuals.ForgetRemovedMedia();
            foreach (string id in _media.Keys)
            {
                WaitForWaveform(visuals, id);
            }
        }

        // Sin compactar, la cola llevaba una entrada por cada onda calculada: 300 al terminar.
        Assert.InRange(visuals.WaveformOrderCountForTests, 0, (2 * max) + 1);
        Assert.True(visuals.GetWaveform("a0", Time.Zero, Time.FromSeconds(1), 4).IsComplete);
    }

    [Fact]
    public void The_queue_keeps_the_most_recent_use_of_each_file_after_compacting()
    {
        const int max = 2;
        using var visuals = new ClipVisuals(id => _media.GetValueOrDefault(id), _sources, new ClipVisualsOptions { MaxWaveformsInMemory = max, ThumbnailWorkers = 1 });
        _media["x"] = @"C:\x.wav";
        _media["y"] = @"C:\y.wav";

        // «x» se olvida y vuelve muchas veces; «y» queda en memoria desde el principio.
        WaitForWaveform(visuals, "y");
        for (int i = 0; i < 20; i++)
        {
            _media.Remove("x");
            visuals.ForgetRemovedMedia();
            _media["x"] = @"C:\x.wav";
            WaitForWaveform(visuals, "x");
        }

        // Una tercera onda expulsa la más vieja (y), no la que se acaba de usar (x).
        _media["z"] = @"C:\z.wav";
        WaitForWaveform(visuals, "z");

        Assert.True(visuals.GetWaveform("x", Time.Zero, Time.FromSeconds(1), 4).IsComplete);
        Assert.InRange(visuals.WaveformOrderCountForTests, 0, (2 * max) + 1);
    }

    private static void WaitForWaveform(ClipVisuals visuals, string id)
    {
        DateTime deadline = DateTime.UtcNow + Patience;
        while (!visuals.GetWaveform(id, Time.Zero, Time.FromSeconds(1), 4).IsComplete)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"La onda de {id} no llegó a tiempo.");
            }

            Thread.Sleep(1);
        }
    }
}
