using Rodaje.App.Services;
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
        (RemoveSilences, "Quitar silencios...", ""),
        (DetectBeats, "Detectar ritmo...", ""),
        (Ducking, "Ducking automático...", ""),
        (Stabilize, "Estabilizar...", ""),
        (AutoColor, "Color automático", ""),
        (WhiteBalance, "Balance de blancos automático", ""),
    ];

    public static IDisposable Bind(ShortcutRouter router, EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(services);
        IDisposable[] bindings =
        [
            router.Bind(RemoveSilences, () => Run(services.Session, () => SilenceDialog.ShowAsync(services))),
            router.Bind(DetectBeats, () => Run(services.Session, () => RhythmDialog.ShowAsync(services))),
            router.Bind(Ducking, () => Run(services.Session, () => DuckingDialog.ShowAsync(services))),
            router.Bind(Stabilize, () => Run(services.Session, () => StabilizeDialog.ShowAsync(services))),
            router.Bind(AutoColor, () => Run(services.Session, () => AutoColorAction.RunAsync(services, levels: true))),
            router.Bind(WhiteBalance, () => Run(services.Session, () => AutoColorAction.RunAsync(services, levels: false))),
            router.Bind(ReduceNoise, () => Run(services.Session, () => NoiseReductionDialog.ShowAsync(services))),
        ];
        if (services.Modules is { } modules)
        {
            // Con modulo-audio-ia instalado, el ducking busca la voz con Silero; si no, con la energía, como antes.
            DuckingDialog.Detector = new Services.Modules.ModuleVoiceDetector(modules, new Rodaje.Analysis.Ducking.EnergyVoiceDetector());
        }

        return new Bindings(bindings);
    }

    private static async void Run(EditorSession session, Func<Task> show)
    {
        try
        {
            await show();
        }
#pragma warning disable CA1031 // Un análisis que falla no debe cerrar la app: se avisa.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            session.Notify(NotificationSeverity.Error, "El análisis falló", ex.Message);
        }
    }

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
