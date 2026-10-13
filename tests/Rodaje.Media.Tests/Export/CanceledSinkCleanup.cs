using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

/// <summary>
/// Lo que una prueba de cancelación espera antes de comprobar que no quedó nada (tarea 263). <see cref="FfmpegPipeSink.Abort"/>
/// vuelve como mucho a los ~900 ms de matar a ffmpeg.exe; si para entonces no salió, deja la limpieza en
/// <see cref="FfmpegPipeSink.PendingCleanup"/>, que borra los temporales y la entrada de <c>exportando.json</c> cuando sale.
/// Comprobar antes de que esa tarea termine ve una entrada (o un temporal) que la limpieza todavía va a quitar.
/// </summary>
/// <remarks>Compartido con <c>Rodaje.Export.Tests</c> (enlazado en su proyecto).</remarks>
internal static class CanceledSinkCleanup
{
    /// <summary>Red de seguridad para no colgar la corrida; no es un plazo de lo que se mide.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>Espera la limpieza pendiente del último Abort de <paramref name="sink"/>, si la dejó.</summary>
    /// <returns>Si hubo limpieza pendiente.</returns>
    public static bool Wait(FfmpegPipeSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (sink.PendingCleanup is not { } pending)
        {
            return false;
        }

        if (!pending.Wait(Bound))
        {
            throw new Xunit.Sdk.XunitException(
                $"La limpieza pendiente de la cancelación no terminó en {Bound.TotalSeconds:0} s: {string.Join(" | ", sink.Diagnostics)}");
        }

        return true;
    }
}
