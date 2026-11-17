using Rodaje.App.Diagnostics;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Player;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Shell;

internal static partial class SelfTestRun
{
    public static async Task RunAsync(MainWindow window, PlayerEngine engine, Metrics metrics)
    {
        int code = 1;
        try
        {
            await CaptureAsync(window, "inicio");
            SelectFirstClip(window);
            foreach (InspectorView.Tab tab in Enum.GetValues<InspectorView.Tab>())
            {
                window.InspectorPane.Show(tab);
                await Task.Delay(250);
                await CaptureAsync(window, "inspector-" + tab.ToString().ToLowerInvariant());
            }

            await ToolsAsync(window);
            await TranscriptAsync(window);
            await PreferencesAsync(window);
            await ShortcutsAsync(window);
            await ShowCurveAsync(window);
            await ToggleReverseAsync(window);
            await InterpolationAsync(window);
            await SampledAsync(window, engine, new Rational(1, 2), FrameSampling.Blend, 0.5f);
            await SampledAsync(window, engine, new Rational(1, 4), FrameSampling.OpticalFlow, 0.75f);
            await TitleAsync(window, engine);
            await TemplatesAsync(window, engine);
            await ImageAsync(window, engine);
            await MasksAsync(window, engine);
            await TrackingAsync(window, engine);
            await TransitionsAsync(window, engine);
            await TransformAsync(window, engine);
            await ColorAsync(window, engine);
            await MixerAsync(window);
            await ShowExportAsync(window);
            await PluginsAsync(window);
            await ModuleAsync(window);
            await FormatAsync(window, engine);
            await InterchangeAsync(window);
            await RenderCacheAsync(window);
            window.InspectorPane.Show(InspectorView.Tab.Properties);
            await LayoutAsync(window);
            if (window.Session.Sequence.Duration.Ticks > 0)
            {
                window.Session.Play();
                for (int i = 0; i < 100 && !metrics.HasMilestone("primer_cuadro") && engine.FailureMessage is null; i++)
                {
                    await Task.Delay(100);
                }

                await Task.Delay(1000);
                window.Session.Pause();
            }

            code = engine.FailureMessage is null && (window.Session.Sequence.Duration.Ticks == 0 || metrics.HasMilestone("primer_cuadro")) ? 0 : 1;
            if (code != 0)
            {
                Program.WriteCrash("autoprueba: " + (engine.FailureMessage ?? "no llegó el primer cuadro en 10 s"));
            }
            else if (!IsUpstreamCreditShown(window))
            {
                Program.WriteCrash("autoprueba: el crédito upstream de la barra de título no se ve (LICENSE, término adicional 1)");
                code = 1;
            }
        }
#pragma warning disable CA1031 // La autoprueba informa cualquier fallo con el código de salida.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Program.WriteCrash("autoprueba: " + ex);
            code = 2;
        }

        WriteReport(metrics);
        Program.ExitCode = code;
        window.Close();
    }
}
