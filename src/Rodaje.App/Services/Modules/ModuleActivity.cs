using Rodaje.App.Services.Operations;

namespace Rodaje.App.Services.Modules;

/// <summary>Lo que muestra un diálogo con su propia barra mientras corre un módulo.</summary>
/// <param name="Progress">De 0 a 1; no cuenta si <paramref name="IsIndeterminate"/>.</param>
public readonly record struct ModuleActivityView(string Text, double Progress, bool IsIndeterminate);

/// <summary>
/// Los estados de un trabajo de módulo que pueden durar sin avance a la vista: el arranque (hasta 30 s en frío,
/// <c>StartTimeout</c>) y la cancelación (hasta 10 s, <c>CancelGrace</c>). Mientras duran, la interfaz lo dice y la barra
/// se mueve sin porcentaje, para que no parezca congelada.
/// </summary>
public static class ModuleActivity
{
    public const string Starting = "Iniciando el módulo…";

    public const string Cancelling = "Cancelando…";

    /// <summary>
    /// Texto y barra de un diálogo: <paramref name="job"/> es nulo hasta que el módulo arrancó (el trabajo se le entrega
    /// entonces); <paramref name="cancelRequested"/>, que el diálogo pidió cancelar; <paramref name="working"/>, lo que
    /// hace cuando el módulo no dice su etapa.
    /// </summary>
    public static ModuleActivityView Describe(ModuleJob? job, bool cancelRequested, string working)
    {
        ArgumentNullException.ThrowIfNull(working);
        double progress = job?.Progress ?? 0;
        if (cancelRequested || job?.IsCancelling == true)
        {
            return new ModuleActivityView(Cancelling, progress, true);
        }

        if (job is null || job.State == ModuleJobState.Waiting)
        {
            return new ModuleActivityView(job?.Stage ?? Starting, progress, true);
        }

        return new ModuleActivityView((job.Stage ?? working) + "…", progress, false);
    }
}

/// <summary>
/// El avance que dibuja un diálogo de módulo: el del trabajo cuando ya existe y, mientras el módulo arranca (hasta 30 s en
/// frío), uno propio «Iniciando el módulo…» que cuenta desde que se pidió; así la barra dice cuánto lleva y, si pasan unos
/// segundos sin avance, que sigue trabajando.
/// </summary>
public sealed class ModuleDialogActivity
{
    private OperationProgress _starting = NewStarting();

    /// <summary>Empieza una corrida nueva (el trabajo anterior ya no cuenta).</summary>
    public void Restart() => _starting = NewStarting();

    public OperationProgress Current(ModuleJob? job, bool cancelRequested)
    {
        if (job is not null)
        {
            return job.Operation;
        }

        if (cancelRequested)
        {
            _starting.MarkCancelling();
        }

        return _starting;
    }

    private static OperationProgress NewStarting()
    {
        var starting = new OperationProgress();
        starting.Report(null, ModuleActivity.Starting);
        return starting;
    }
}
