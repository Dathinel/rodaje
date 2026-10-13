using Rodaje.Engine.Timeline;
using Rodaje.Render;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Miniaturas animadas de las transiciones: cada cuadro compuesto en la GPU con el mismo shader que la línea de tiempo, entre
/// dos imágenes de muestra, en el hilo de render y con un compositor propio del tamaño de la miniatura.
/// </summary>
internal sealed partial class PlayerEngine
{
    private Compositor? _transitionStills;

    /// <summary>Compone cada cuadro entre <paramref name="outgoing"/> y <paramref name="incoming"/> (RGBA de 8 bits) y devuelve los RGBA.</summary>
    public Task<IReadOnlyList<byte[]>> RenderTransitionAsync(int width, int height, byte[] outgoing, byte[] incoming,
        IReadOnlyList<TransitionPreviewFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(outgoing);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(frames);
        var done = new TaskCompletionSource<IReadOnlyList<byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gpuWork.Enqueue(() =>
        {
            try
            {
                if (_gpu is null)
                {
                    throw new InvalidOperationException("El reproductor no tiene GPU.");
                }

                if (_transitionStills is not { } compositor || compositor.Width != width || compositor.Height != height)
                {
                    _transitionStills?.Dispose();
                    _transitionStills = compositor = new Compositor(_gpu, width, height);
                }

                compositor.Resources = _content;
                if (!compositor.ShadersReady.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Los shaders de efectos no estuvieron listos.");
                }

                var images = new List<byte[]>(frames.Count);
                foreach (TransitionPreviewFrame frame in frames)
                {
                    compositor.RenderTransitionStill(outgoing, incoming, frame.Mix, frame.Outgoing, frame.Incoming);
                    images.Add(compositor.ReadbackRgba());
                }

                done.SetResult(images);
            }
#pragma warning disable CA1031 // La falla va a quien espera las miniaturas, no tumba el hilo de render.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                done.SetException(ex);
            }
        });
        _wake.Set();
        return done.Task;
    }
}
