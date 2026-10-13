namespace Rodaje.Core.Model;

/// <summary>
/// Lo que se acotó al abrir un proyecto de la v1.0.0, por clase, para que el aviso diga qué cambió y cuánto
/// (<see cref="TimelineLimits.Normalize(Project)"/> y <see cref="Rodaje.Core.Effects.ParameterLimits.Normalize(Project)"/>).
/// </summary>
/// <param name="FrameRates">Secuencias cuya tasa de cuadros se llevó al rango admitido.</param>
/// <param name="TrimmedClips">Clips que pasaban del final de la línea de tiempo y se recortaron hasta él.</param>
/// <param name="RemovedClips">
/// Clips que pasaban del final y se quitaron: los que empiezan después del final y los que tienen velocidad. Se guardan
/// enteros para que el aviso pueda nombrarlos (su medio, su título o su compuesto siguen en el proyecto).
/// </param>
/// <param name="Values">Valores de efecto (posición, ancla, giro, también en keyframes) llevados al límite.</param>
public sealed record ClampSummary(int FrameRates, int TrimmedClips, IReadOnlyList<Clip> RemovedClips, int Values)
{
    /// <summary>Nada acotado.</summary>
    public static ClampSummary None { get; } = new(0, 0, [], 0);

    /// <summary>Todo lo acotado, contado como antes: una tasa, un clip recortado o quitado, o un valor, cuenta uno.</summary>
    public int Total => FrameRates + TrimmedClips + RemovedClips.Count + Values;
}
