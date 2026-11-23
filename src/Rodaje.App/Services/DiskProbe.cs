namespace Rodaje.App.Services;

/// <summary>
/// Preguntas al disco que pueden no volver nunca: con un recurso de red caído, <c>File.Exists</c> o la fecha de un archivo
/// esperan al tiempo de espera de SMB (decenas de segundos). Se hacen en un hilo del pool y, si tardan más de
/// <see cref="Limit"/>, se sigue con la respuesta de respaldo; la pregunta colgada termina sola cuando Windows se rinde.
/// </summary>
public static class DiskProbe
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);

    public static async Task<T> WithinAsync<T>(Func<T> probe, T fallback, TimeSpan? limit = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        try
        {
            return await Task.Run(probe).WaitAsync(limit ?? Limit).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return fallback;
        }
    }
}
