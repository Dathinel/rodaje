namespace Rodaje.Analysis.Video.Interpolation;

/// <summary>Resultado de la interpolación de cuadros con métricas de corte de escena y residuo.</summary>
public sealed record FlowOutcome(PlanarFrame Frame, bool SceneCut, float HistogramDistance, float Residual);
