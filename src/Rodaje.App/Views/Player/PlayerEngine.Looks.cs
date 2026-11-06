using System.Collections.Concurrent;
using Rodaje.Playback.Composition;
using Rodaje.Render;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Miniaturas de los presets: una imagen chica del cuadro actual compuesta con los efectos de cada preset, en el hilo de
/// render y con un compositor propio del tamaño de la miniatura (se guarda mientras no cambie el tamaño).
/// </summary>
internal sealed partial class PlayerEngine
{
    private readonly ConcurrentQueue<Action> _gpuWork = new();
    private Compositor? _looks;

    /// <summary>
    /// Compone <paramref name="source"/> (RGBA de 8 bits) con cada lista de efectos y devuelve las imágenes RGBA en el mismo
    /// orden. Desde cualquier hilo; corre en el de render.
    /// </summary>
    public Task<IReadOnlyList<byte[]>> RenderLooksAsync(FrameCapture source, IReadOnlyList<IReadOnlyList<LayerEffect>> looks)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(looks);
        var done = new TaskCompletionSource<IReadOnlyList<byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gpuWork.Enqueue(() =>
        {
            try
            {
                if (_gpu is null)
                {
                    throw new InvalidOperationException("El reproductor no tiene GPU.");
                }

                if (_looks is not { } compositor || compositor.Width != source.Width || compositor.Height != source.Height)
                {
                    _looks?.Dispose();
                    _looks = compositor = new Compositor(_gpu, source.Width, source.Height);
                }

                compositor.Resources = _content;
                if (!compositor.ShadersReady.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Los shaders de efectos no estuvieron listos.");
                }

                var images = new List<byte[]>(looks.Count);
                foreach (IReadOnlyList<LayerEffect> look in looks)
                {
                    compositor.RenderStill(source.Rgba, look);
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
        Wake();
        return done.Task;
    }

    private void RunGpuWork()
    {
        while (_gpuWork.TryDequeue(out Action? work))
        {
            work();
        }
    }
}
