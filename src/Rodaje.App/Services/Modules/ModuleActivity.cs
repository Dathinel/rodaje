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
