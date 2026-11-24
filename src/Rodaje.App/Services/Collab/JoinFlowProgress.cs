using Rodaje.App.Services.Operations;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// Las fases que ve quien entra en una sala, de principio a fin y con la misma numeración en los tres diálogos del
/// camino: entrar en la sala (1), los pasos con que llega el proyecto del anfitrión (2 a <see cref="Steps"/> - 1,
/// <see cref="ProjectShareGuest.Steps"/>) y buscar los medios en este equipo (<see cref="SearchStep"/>).
/// </summary>
public static class JoinFlowProgress
{
    /// <summary>Las fases del camino entero: entrar, las del reparto del proyecto y buscar los medios.</summary>
    public const int Steps = 1 + ProjectShareGuest.Steps + 1;

    /// <summary>La fase de buscar los medios (la última); su avance va por <see cref="OperationProgress.Report"/>.</summary>
    public const int SearchStep = Steps;

    /// <summary>«Paso 1 de 5»: conectar y esperar a que quien hospeda deje entrar. Sin porcentaje: no se sabe cuánto tardará.</summary>
    public static void Begin(OperationProgress operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        operation.SetPhase(1, Steps, "Conectando con la sala");
    }

    /// <summary>La última fase: buscar los medios del proyecto por su contenido en las carpetas elegidas.</summary>
    public static void Searching(OperationProgress operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        operation.SetPhase(SearchStep, Steps, "Buscando tus archivos");
    }

    /// <summary>
    /// Los pasos del reparto (<see cref="ShareProgress"/>) como fases 2 a <see cref="Steps"/> - 1 del camino: la fracción
    /// de cada paso se reparte dentro del total, así la barra no retrocede al cambiar de fase y el tiempo restante sale
    /// del conjunto.
    /// </summary>
    public static IProgress<ShareProgress> Receive(OperationProgress operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return new ToFlow(operation);
    }

    private sealed class ToFlow(OperationProgress operation) : IProgress<ShareProgress>
    {
        public void Report(ShareProgress value)
        {
            int phase = value.Step + 1;
            operation.SetPhase(phase, Steps, value.Text);
            operation.Report(value.Fraction is { } f ? OperationProgress.PhaseFraction(phase, Steps, f) : null);
        }
    }
}
