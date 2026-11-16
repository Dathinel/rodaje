namespace Rodaje.Analysis.Video.Interpolation;

/// <summary>Parámetros configurables del algoritmo de flujo óptico DIS y de interpolación.</summary>
public sealed record FlowParameters
{
    /// <summary>Nivel de pirámide más fino en el que se calcula el flujo (¼ de resolución por defecto).</summary>
    public int FlowLevel { get; init; } = 2;

    /// <summary>Tamaño del parche cuadrado en píxeles.</summary>
    public int PatchSize { get; init; } = 8;

    /// <summary>Paso de desplazamiento entre parches consecutivos.</summary>
    public int PatchStride { get; init; } = 4;

    /// <summary>Número máximo de iteraciones de Gauss-Newton inverso.</summary>
    public int Iterations { get; init; } = 12;

    /// <summary>Umbral de convergencia por norma del cambio de flujo |Δu|.</summary>
    public float DeltaThreshold { get; init; } = 0.01f;

    /// <summary>Regularización añadida a la diagonal del Hessiano 2×2.</summary>
    public float DiagonalRegularization { get; init; } = 1e-4f;

    /// <summary>Umbral de distancia de histograma de 64 cajas para corte de escena.</summary>
    public float CutHistogram { get; init; } = 0.5f;

    /// <summary>Umbral de residuo medio tras deformar en FlowLevel para corte de escena.</summary>
    public float CutResidual { get; init; } = 0.08f;

    /// <summary>Término de escala en el umbral de oclusión por consistencia (Sundaram et al.).</summary>
    public float Alpha { get; init; } = 0.01f;

    /// <summary>Término constante en el umbral de oclusión por consistencia (Sundaram et al.).</summary>
    public float Beta { get; init; } = 0.5f;
}
