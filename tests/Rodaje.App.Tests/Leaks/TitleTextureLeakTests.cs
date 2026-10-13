using System.Diagnostics;
using Rodaje.App.Services.Titles;
using Rodaje.App.Tests.Support;
using Rodaje.App.Tests.TimelineLogic;
using Rodaje.App.Views.Player;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.Render.Text;

namespace Rodaje.App.Tests.Leaks;

/// <summary>
/// Las texturas de los títulos viven en la caché del rasterizador, que es del reproductor y no del proyecto. Antes, al
/// abrir otro proyecto, las del anterior quedaban en la VRAM hasta que el tope de la caché las expulsaba; ahora
/// <c>PlayerEngine</c> carga cada proyecto con <see cref="ProjectSwitch.Load"/>, que las suelta, y aquí se carga igual.
/// </summary>
[Collection(GpuCompositionGroup.Name)]
public sealed class TitleTextureLeakTests
{
    private const int Width = 320;
    private const int Height = 180;
    private static readonly int Cycles = Rodaje.TestMedia.Skip.LongRuns ? 100 : 20;

    // Espera a los shaders y a cada título en bloque: en un hilo propio, no en el del pool en que xUnit corre la prueba.
    [GpuFact]
    public Task Opening_project_after_project_keeps_only_the_titles_of_the_last_two() => OwnThread.Run(OpenProjectAfterProject);

    private static void OpenProjectAfterProject()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu);
        using var ready = new ManualResetEventSlim();
        titles.Changed += (_, _) => ready.Set();
        using var compositor = new Compositor(gpu, Width, Height) { Titles = titles };
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)), "Los shaders no compilaron en 60 s.");

        int worst = 0;
        TimelineSession? session = null;
        try
        {
            for (int cycle = 0; cycle < Cycles; cycle++)
            {
                // Como PlayerEngine al cargar otro proyecto: cierra la sesión anterior, abre la nueva y suelta los títulos.
                Project project = TitleProject(cycle);
                ProjectSwitch.Load(
                    () =>
                    {
                        session?.Dispose();
                        session = null;
                        session = TimelineSession.Open(project, "s", gpu,
                            new TimelineSessionOptions { EnableAudio = false, UseProxies = ProxyUsage.Never, Titles = titles });
                    },
                    titles);
                TimelineSession current = session!;
                current.Seek(Time.FromSeconds(1));
                var watch = Stopwatch.StartNew();
                CompositeResult result;
                do
                {
                    // Tope contra cuelgues: en la CI el título se rasteriza en otro hilo y se compone con WARP en la CPU.
                    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"El título de la vuelta {cycle} no llegó en 60 s.");
                    ready.Wait(TimeSpan.FromMilliseconds(20));
                    using CompositionFrame frame = current.Tick();
                    result = compositor.Render(frame);
                }
                while (current.IsSeekPending || result.TitlesPending > 0);

                Assert.Equal(1, result.LayersDrawn);

                // Un cuadro más con el título ya dibujado: así el de la vuelta anterior quedó al menos dos cuadros atrás y
                // la cuenta no depende de cuántos cuadros tardó en llegar este.
                using (CompositionFrame frame = current.Tick())
                {
                    Assert.Equal(1, compositor.Render(frame).LayersDrawn);
                }

                worst = Math.Max(worst, titles.CachedCount);
            }
        }
        finally
        {
            session?.Dispose();
        }

        // El título de este proyecto y el del anterior, que se usó en el último cuadro antes de cargar. Sin soltarlos
        // quedaba una textura por proyecto abierto: 20 al terminar.
        Assert.Equal(2, worst);
    }

    private static Project TitleProject(int cycle)
    {
        TextStyle style = TitleStyles.Default();
        style.Size = 48;
        var track = new Track { Id = "v", Kind = TrackKind.Video, Name = "Texto" };
        track.Clips.Add(new Clip
        {
            Id = "t",
            Start = Time.Zero,
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(5),
            Generator = new ClipGenerator { Text = $"Proyecto {cycle}", Style = style },
        });
        var sequence = new Sequence { Id = "s", Name = "Títulos", FrameRate = Rational.Fps30, Width = Width, Height = Height };
        sequence.VideoTracks.Add(track);
        return new Project { Id = $"p{cycle}", Name = $"Proyecto {cycle}", Sequences = [sequence] };
    }
}
