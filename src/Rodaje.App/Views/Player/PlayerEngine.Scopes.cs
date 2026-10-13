using System.Diagnostics;
using Rodaje.App.Services.Crashes;
using Rodaje.Render;
using Rodaje.Render.Scopes;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Monitores de imagen del reproductor: tras componer, el hilo de render encola la medición del cuadro (como mucho 30 por
/// segundo) y en cada vuelta recoge la imagen que la GPU ya terminó; la interfaz la toma con <see cref="TakeScope"/>.
/// También la vista de la máscara del calificador, que es solo de la vista previa.
/// </summary>
internal sealed partial class PlayerEngine
{
    private static readonly TimeSpan ScopeInterval = TimeSpan.FromMilliseconds(33);

    private ScopeRenderer? _scopes;
    private int _scopeKind = -1;
    private long _lastScopeSubmit;
    private ScopeImage? _latestScope;
    private int _showQualifierMask;

    /// <summary>El monitor que se mide, o <c>null</c> para apagarlos. Desde cualquier hilo.</summary>
    public void SetScope(ScopeKind? kind)
    {
        Volatile.Write(ref _scopeKind, kind is { } k ? (int)k : -1);
        _dirty = true;
        _wake.Set();
    }

    /// <summary>La imagen de monitor más nueva desde la última vez, o <c>null</c>. Desde cualquier hilo.</summary>
    public ScopeImage? TakeScope() => Interlocked.Exchange(ref _latestScope, null);

    /// <summary>Lo que costó en la CPU encolar la última medición (ms).</summary>
    public double ScopeSubmitMilliseconds => _scopes?.LastSubmitMilliseconds ?? 0;

    /// <summary>Solo vista previa: el calificador HSL se ve como su máscara. Desde cualquier hilo.</summary>
    public void SetQualifierMaskView(bool show)
    {
        Volatile.Write(ref _showQualifierMask, show ? 1 : 0);
        _dirty = true;
        _wake.Set();
    }

    private void SubmitScope(Compositor compositor)
    {
        int kind = Volatile.Read(ref _scopeKind);
        if (kind < 0 || _gpu is null)
        {
            return;
        }

        bool playing = _session is { IsPlaying: true };
        if (playing && Stopwatch.GetElapsedTime(_lastScopeSubmit) < ScopeInterval)
        {
            return;
        }

        if (_scopes is null)
        {
            // Una sola continuación, al crearlo, con los shaders compilados o no: si no compilan, los monitores quedan vacíos
            // y el error (solo en Ready) se informa una vez, sin aviso; si compilan, se vuelve a componer para medir el cuadro
            // en pausa. La compilación puede terminar después de Dispose, con el evento ya liberado: no hay a quién despertar.
            _scopes = new ScopeRenderer(_gpu);
            UiTask.Run(ScopeReadiness.Watch(_scopes.Ready, failure => UiTask.Report(failure, notify: false), () =>
            {
                _dirty = true;
                WakeUnlessDisposed();
            }));
        }

        if (_scopes.Submit(compositor, (ScopeKind)kind))
        {
            _lastScopeSubmit = Stopwatch.GetTimestamp();
        }
    }

    private void WakeUnlessDisposed()
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // Dispose ganó la carrera entre la comprobación y Set: el hilo de render ya terminó.
        }
    }

    private void PumpScopes()
    {
        if (_scopes?.TryRead() is { } image)
        {
            Volatile.Write(ref _latestScope, image);
        }
    }
}
