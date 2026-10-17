namespace Rodaje.App.Views.Analysis;

/// <summary>Disponibilidad del botón principal mientras se prepara un resultado de análisis.</summary>
public enum AnalysisApplyPhase
{
    NeedsAnalysis,
    Analyzing,
    Ready,
    Blocked,
}

public static class AnalysisApplyState
{
    /// <summary>La acción principal sigue disponible durante el análisis; solo faltan requisitos si está bloqueada.</summary>
    public static bool IsEnabled(AnalysisApplyPhase phase) => phase != AnalysisApplyPhase.Blocked;
}
