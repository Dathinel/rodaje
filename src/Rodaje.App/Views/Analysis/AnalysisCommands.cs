using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Analysis;

/// <summary>Acciones de análisis de audio y de imagen («Editar» y el menú del clip). Sin atajo por omisión; sin un clip adecuado seleccionado, avisan.</summary>
internal static partial class AnalysisCommands
{
    public const string RemoveSilences = "analysis.silences";
    public const string DetectBeats = "analysis.beats";
    public const string Ducking = "analysis.ducking";
    public const string Stabilize = "analysis.stabilize";
    public const string AutoColor = "analysis.autocolor";
    public const string WhiteBalance = "analysis.whitebalance";
    public const string ReduceNoise = "analysis.noise";

    public static IReadOnlyList<(string Id, string Label, string Glyph)> Menu { get; } =
    [
        (RemoveSilences, "Quitar silencios…", ""),
        (DetectBeats, "Detectar ritmo…", ""),
        (Ducking, "Bajar música bajo la voz…", ""),
        (Stabilize, "Estabilizar…", ""),
        (AutoColor, "Color automático", ""),
        (WhiteBalance, "Balance de blancos automático", ""),
    ];

    public static IDisposable Bind(ShortcutRouter router, EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(services);
        IDisposable[] bindings =
        [
            router.Bind(RemoveSilences, () => Run(services, RemoveSilences, "Quitar silencios", scope => SilenceDialog.ShowAsync(services, scope))),
            router.Bind(DetectBeats, () => Run(services, DetectBeats, "Detectar ritmo", scope => RhythmDialog.ShowAsync(services, scope))),
            router.Bind(Ducking, () => Run(services, Ducking, DuckingDialog.Title, scope => DuckingDialog.ShowAsync(services, scope))),
            router.Bind(Stabilize, () => Run(services, Stabilize, "Estabilizar", scope => StabilizeDialog.ShowAsync(services, scope))),
            router.Bind(AutoColor, () => Run(services, AutoColor, "Color automático", scope => AutoColorAction.RunAsync(services.Session, levels: true, scope), canCancel: true)),
            router.Bind(WhiteBalance, () => Run(services, WhiteBalance, "Balance de blancos automático",
                scope => AutoColorAction.RunAsync(services.Session, levels: false, scope), canCancel: true)),
            router.Bind(ReduceNoise, () => Run(services, ReduceNoise, "Reducir ruido", scope => NoiseReductionDialog.ShowAsync(services, scope))),
        ];
        if (services.Modules is { } modules)
        {
            // El VAD de módulo se comparte entre silencios y ducking; los dos conservan la energía como respaldo.
            var detector = new Services.Modules.ModuleVoiceDetector(modules, new Rodaje.Analysis.Ducking.EnergyVoiceDetector());
            DuckingDialog.Detector = detector;
            SilenceDialog.Detector = detector;
        }

        return new Bindings(bindings);
    }

    /// <summary>Cada análisis corre como acción larga: su botón queda ocupado y la barra de avisos muestra el avance.</summary>
    private static void Run(EditorServices services, string id, string title, Func<LongActionScope, Task> work, bool canCancel = false) =>
        UiTask.Run(services.LongActions.RunAsync(id, title, work, canCancel));

    private sealed partial class Bindings(IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (IDisposable item in items)
            {
                item.Dispose();
            }
        }
    }
}
