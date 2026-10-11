using Rodaje.App.Services;
using Rodaje.App.Services.Settings;
using Rodaje.Engine.Timeline;
using Rodaje.Export.Running;

namespace Rodaje.App.Views.Export;

/// <summary>
/// Lo que la vista previa cede mientras se exporta (D-028): en alto rendimiento se pausa; con menos de 4 GB de VRAM pasa a
/// proxies y a Mitad hasta que termina, y luego vuelve a lo que diga el ajuste en ese momento.
/// </summary>
/// <param name="player">El reproductor; sus órdenes se pueden encolar desde cualquier hilo.</param>
/// <param name="setQuality">Calidad de la vista previa sin guardarla en los ajustes.</param>
/// <param name="settings">Los ajustes actuales (proxies y calidad elegidos por la persona).</param>
public sealed partial class PreviewYield(IPlaybackEngine player, Action<PreviewQuality> setQuality, Func<AppSettings> settings) : IExportPreview
{
    private int _lowered;

    public IDisposable Yield(ExportPerformanceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.PausePreview)
        {
            player.Pause();
        }

        if (!profile.PreviewProxies)
        {
            return Restore.Nothing;
        }

        if (Interlocked.Increment(ref _lowered) == 1)
        {
            player.SetProxyUsage(ProxyUsage.Always);
            setQuality(PreviewQuality.Half);
        }

        return new Restore(() =>
        {
            if (Interlocked.Decrement(ref _lowered) == 0)
            {
                AppSettings current = settings();
                player.SetProxyUsage(current.ProxyUsage);
                setQuality(current.PreviewQuality);
            }
        });
    }

    private sealed partial class Restore(Action? undo) : IDisposable
    {
        public static Restore Nothing { get; } = new(null);

        private Action? _undo = undo;

        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }
}
