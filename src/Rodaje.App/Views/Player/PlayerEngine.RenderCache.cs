using Rodaje.App.Services.RenderCache;
using Rodaje.Engine.RenderCache;
using Rodaje.Playback.Composition;

namespace Rodaje.App.Views.Player;

internal sealed partial class PlayerEngine : IRenderCachePlayer
{
    private int _generation;
    private (int Generation, CacheCoverage Coverage)? _pendingCoverage;

    /// <summary>Se abrió o recargó un plan (para el render en caché). Llega desde el hilo de render.</summary>
    public event Action<RenderCachePlan>? PlanChanged;

    /// <summary>Un medio no se pudo leer al reproducir (por ejemplo, un fragmento del caché). Llega desde el hilo del decodificador.</summary>
    public event Action<string>? SourceFailed;

    public IReadOnlyList<long> LateFrames => LatestStats?.LateFrames ?? [];

    public bool HasGpu => Volatile.Read(ref _gpu) is { IsSoftware: false };

    /// <summary>Si a un título le falta alguna fuente (se vería con otra); sin el rasterizador aún, se da por que falta.</summary>
    public bool IsFontMissing(GeneratedContent content)
    {
        try
        {
            return Volatile.Read(ref _titles) is not { } titles || titles.FindMissingFonts([content]).Count > 0;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    public void ApplyCoverage(int generation, CacheCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        lock (_pendingLock)
        {
            _pendingCoverage = (generation, coverage);
        }

        Wake();
    }

    private void RaisePlanChanged(bool opened)
    {
        if (opened)
        {
            _generation++;
        }

        if (_session is { } session && _project is { } project && _sequenceId is { } sequenceId)
        {
            PlanChanged?.Invoke(new RenderCachePlan(project, sequenceId, _generation, session.PlanRevision, session.CurrentFrame));
        }
    }

    private void ApplyPendingCoverage()
    {
        (int Generation, CacheCoverage Coverage)? pending;
        lock (_pendingLock)
        {
            pending = _pendingCoverage;
            _pendingCoverage = null;
        }

        if (pending is { } next && next.Generation == _generation && _session is { } session && session.ApplyCoverage(next.Coverage))
        {
            _dirty = true;
        }
    }
}
