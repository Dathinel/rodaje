using Rodaje.Playback.Composition;
using TerraFX.Interop.DirectX;

namespace Rodaje.Render;

public sealed unsafe partial class Compositor
{
    /// <summary>
    /// Compone una imagen fija RGBA de 8 bits (opaca, del tamaño del compositor) como una capa que llena el cuadro, con esos
    /// efectos: las miniaturas de los presets sobre el cuadro actual. Se lee con <see cref="ReadbackRgba"/>.
    /// </summary>
    public CompositeResult RenderStill(ReadOnlySpan<byte> rgba, IReadOnlyList<LayerEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ObjectDisposedException.ThrowIf(_output is null, this);
        if (rgba.Length < Width * Height * 4)
        {
            throw new ArgumentException("La imagen no tiene el tamaño del compositor.", nameof(rgba));
        }

        var half = new Half[Width * Height * 4];
        for (int i = 0; i < half.Length; i++)
        {
            half[i] = (Half)(rgba[i] / 255f);
        }

        var stats = default(FrameStats);
        int drawn = 0;
        using var layer = new LayerFrame(0, "still", [], LayerTransform.Identity) { Effects = effects };
        _gpu.LockContext();
        RenderTarget? source = null;
        try
        {
            source = _pool.Rent(Width, Height, LayerRenderer.IntermediateFormat, exact: true);
            if (source is null)
            {
                return new CompositeResult(0, 1);
            }

            fixed (Half* data = half)
            {
                _context->UpdateSubresource((ID3D11Resource*)source.Texture, 0, null, data, (uint)(Width * 8), 0);
            }

            float* black = stackalloc float[] { 0f, 0f, 0f, 1f };
            _context->ClearRenderTargetView(_output.Rtv, black);
            var destination = new Destination(_output, Width, Height, Width, Height);
            if (_layers.Draw(layer, LayerSource.Of(new ImageView(source, Width, Height)), null, null, destination, ref stats))
            {
                drawn = 1;
            }
        }
        finally
        {
            _pool.Return(source);
            _layers.EndFrame();
            _pipeline.Unbind();
            _pool.EndFrame();
            _gpu.UnlockContext();
        }

        return new CompositeResult(drawn, stats.LayersSkipped, stats.EffectsApplied, stats.EffectsSkipped, stats.MissingContent, _pool.Bytes);
    }
}
