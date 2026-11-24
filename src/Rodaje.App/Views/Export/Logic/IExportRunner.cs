using Rodaje.Export;
using Rodaje.Media.Export;
using Rodaje.Media.Video;

namespace Rodaje.App.Views.Export;

/// <summary>
/// Lo que la interfaz necesita de la orquestación (<c>Rodaje.Export</c>): qué codificadores
/// funcionan en este equipo y lanzar una exportación. Hasta que exista, la app usa <see cref="SimulatedExportRunner"/>.
/// </summary>
public interface IExportRunner
{
    /// <summary>Si es la simulación (no escribe archivos); el diálogo y el progreso lo dicen.</summary>
    bool IsSimulation { get; }

    /// <summary>Codificadores que pasaron la sonda, en orden de preferencia. Puede tardar la primera vez; luego queda en caché.</summary>
    Task<IReadOnlyList<EncoderSpec>> GetEncodersAsync(CancellationToken cancellationToken = default);

    /// <summary>Arranca la exportación en segundo plano y vuelve enseguida.</summary>
    /// <param name="continueWithoutMissing">
    /// Tras un <see cref="ExportErrorCode.ContentMissing"/> o <see cref="ExportErrorCode.EffectFailed"/>, el usuario eligió
    /// «Exportar sin él»: lo que falta se salta como en la vista previa.
    /// </param>
    IExportJob Start(ExportRequest request, bool continueWithoutMissing = false);
}

/// <summary>Una exportación en curso o terminada.</summary>
public interface IExportJob : IDisposable
{
    ExportRequest Request { get; }

    ExportProgress Progress { get; }

    /// <summary>Por qué falló, cuando <see cref="ExportProgress.State"/> es <see cref="ExportJobState.Failed"/>.</summary>
    ExportException? Failure { get; }

    /// <summary>El último cuadro compuesto, reducido (unos 160 px de ancho); <c>null</c> sin video o antes del primero.</summary>
    BgraImage? Thumbnail { get; }

    /// <summary>Cambió el avance, el estado o la miniatura. Llega desde cualquier hilo.</summary>
    event EventHandler? Changed;

    /// <summary>Termina (sin excepción) cuando el trabajo llega a un estado final.</summary>
    Task Completion { get; }

    /// <summary>Lo medido en la exportación que vale la pena decir al terminar (MaxCLL/MaxFALL); <c>null</c> si nada.</summary>
    string? MeasuredNote => null;

    /// <summary>Pide cancelar; el trabajo borra el temporal y pasa a <see cref="ExportJobState.Canceled"/>.</summary>
    void Cancel();
}
