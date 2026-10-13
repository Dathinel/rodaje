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
        CheckSize(rgba);
        var stats = default(FrameStats);
        int drawn = 0;
        using var layer = new LayerFrame(0, "still", [], LayerTransform.Identity) { Effects = effects };
        _gpu.LockContext();
        RenderTarget? source = null;
        try
        {
            source = Upload(rgba);
            if (source is null)
            {
                return new CompositeResult(0, 1);
            }

            ClearOutput();
            var destination = new Destination(_output, Width, Height, Width, Height);
            if (_layers.Draw(layer, LayerSource.Of(new ImageView(source, Width, Height)), null, null, destination, ref stats))
            {
                drawn = 1;
            }
        }
        finally
        {
            _pool.Return(source);
            EndStill();
        }

        return new CompositeResult(drawn, stats.LayersSkipped, stats.EffectsApplied, stats.EffectsSkipped, stats.MissingContent, _pool.Bytes);
    }

    /// <summary>
    /// Un cuadro de una transición entre dos imágenes fijas RGBA de 8 bits del tamaño del compositor, con la misma mezcla que
    /// la línea de tiempo (<paramref name="mix"/>, cuyo clip que entra se ignora) y la transformación de cada lado (las curvas
    /// de un paquete): las miniaturas animadas de la galería y del inspector. Se lee con <see cref="ReadbackRgba"/>.
    /// </summary>
    public CompositeResult RenderTransitionStill(ReadOnlySpan<byte> outgoing, ReadOnlySpan<byte> incoming, LayerTransition mix,
        LayerTransform outgoingTransform, LayerTransform incomingTransform)
    {
        ObjectDisposedException.ThrowIf(_output is null, this);
        CheckSize(outgoing);
        CheckSize(incoming);
        var stats = default(FrameStats);
        int drawn = 0;
        var entering = new LayerFrame(1, "b", [], incomingTransform);
        using var layer = new LayerFrame(0, "a", [], outgoingTransform) { Transition = mix with { Incoming = entering } };
        _gpu.LockContext();
        RenderTarget? a = null;
        RenderTarget? b = null;
        try
        {
            a = Upload(outgoing);
            b = a is null ? null : Upload(incoming);
            if (a is null || b is null)
            {
                return new CompositeResult(0, 1);
            }

            ClearOutput();
            var destination = new Destination(_output, Width, Height, Width, Height);
            if (_layers.Draw(layer, LayerSource.Of(new ImageView(a, Width, Height)), LayerSource.Of(new ImageView(b, Width, Height)), null,
                destination, ref stats))
            {
                drawn = 1;
            }
        }
        finally
        {
            _pool.Return(a);
            _pool.Return(b);
            EndStill();
        }

        return new CompositeResult(drawn, stats.LayersSkipped, stats.EffectsApplied, stats.EffectsSkipped, stats.MissingContent, _pool.Bytes);
    }

    private void CheckSize(ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length < Width * Height * 4)
        {
            throw new ArgumentException("La imagen no tiene el tamaño del compositor.", nameof(rgba));
        }
    }

    /// <summary>Sube la imagen a un intermedio del pool (en RGBA16F), o nulo si no hay memoria.</summary>
    private RenderTarget? Upload(ReadOnlySpan<byte> rgba)
    {
        RenderTarget? target = _pool.Rent(Width, Height, LayerRenderer.IntermediateFormat, exact: true);
        if (target is null)
        {
            return null;
        }

        var half = new Half[Width * Height * 4];
        for (int i = 0; i < half.Length; i++)
        {
            half[i] = (Half)(rgba[i] / 255f);
        }

        fixed (Half* data = half)
        {
            _context->UpdateSubresource((ID3D11Resource*)target.Texture, 0, null, data, (uint)(Width * 8), 0);
        }

        return target;
    }

    private void ClearOutput()
    {
        float* black = stackalloc float[] { 0f, 0f, 0f, 1f };
        _context->ClearRenderTargetView(_output!.Rtv, black);
    }

    private void EndStill()
    {
        _layers.EndFrame();
        _pipeline.Unbind();
        _pool.EndFrame();
        _gpu.UnlockContext();
    }
}
