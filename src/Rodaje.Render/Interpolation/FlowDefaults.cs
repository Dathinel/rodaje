namespace Rodaje.Render.Interpolation;

internal static class FlowDefaults
{
    public const int FlowLevel = 2;
    public const int PatchSize = 8;
    public const int PatchStride = 4;
    public const int Iterations = 12;
    public const float DeltaThreshold = 0.01f;
    public const float DiagonalRegularization = 1e-4f;
    public const float CutHistogram = 0.5f;
    public const float CutResidual = 0.08f;
    public const float Alpha = 0.01f;
    public const float Beta = 0.5f;
}
