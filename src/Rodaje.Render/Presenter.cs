using Rodaje.Media.Gpu;
using TerraFX.Interop.DirectX;

namespace Rodaje.Render;

/// <summary>Dibuja la composición en el búfer de una cadena de intercambio, centrada y con barras del color de <see cref="Surround"/>.</summary>
/// <remarks>
/// Si el destino es más pequeño que la composición genera sus mipmaps y muestrea trilinealmente, para que una vista
/// previa reducida no tenga aliasing; a tamaño igual o mayor muestrea bilinealmente el nivel 0.
/// </remarks>
public sealed unsafe class Presenter : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly ID3D11DeviceContext* _context;
    private readonly QuadPipeline _pipeline;

    public Presenter(GpuDevice gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        _gpu = gpu;
        _context = (ID3D11DeviceContext*)gpu.NativeContext;
        _pipeline = new QuadPipeline((ID3D11Device*)gpu.NativeDevice, _context);
    }

    /// <summary>
    /// Limpia <paramref name="renderTargetView"/> con <see cref="Surround"/> y dibuja encima la composición ajustada. Al volver no queda
    /// ningún destino enlazado: quien llama enlaza el suyo para dibujar lo demás (barras, textos) y presenta.
    /// </summary>
    /// <param name="renderTargetView"><c>ID3D11RenderTargetView*</c> del búfer de la cadena, de este dispositivo.</param>
    /// <returns>El rectángulo donde quedó la imagen.</returns>
    public PixelRect Draw(nint renderTargetView, int targetWidth, int targetHeight, Compositor source) =>
        Draw(renderTargetView, targetWidth, targetHeight, source, null);

    /// <summary>Color de lo que queda fuera de la imagen (R, G, B de 0 a 1); negro por omisión.</summary>
    public (float R, float G, float B) Surround { get; set; }

    /// <summary>Como <see cref="Draw(nint, int, int, Compositor)"/>, pero en <paramref name="placement"/> si se da (puede salirse del destino).</summary>
    public PixelRect Draw(nint renderTargetView, int targetWidth, int targetHeight, Compositor source, PixelRect? placement)
    {
        ArgumentOutOfRangeException.ThrowIfZero(renderTargetView);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Device != _gpu)
        {
            throw new ArgumentException("La composición es de otro dispositivo.", nameof(source));
        }

        var target = (ID3D11RenderTargetView*)renderTargetView;
        PixelRect rect = placement ?? VideoLayout.Fit(targetWidth, targetHeight, source.Width, source.Height);
        _gpu.LockContext();
        try
        {
            float* surround = stackalloc float[] { Surround.R, Surround.G, Surround.B, 1f };
            _context->ClearRenderTargetView(target, surround);
            if (rect.IsEmpty)
            {
                return rect;
            }

            var shaderView = (ID3D11ShaderResourceView*)source.ShaderResourceView;
            bool reduce = rect.Width < source.Width || rect.Height < source.Height;
            if (reduce)
            {
                _context->GenerateMips(shaderView);
            }

            _pipeline.Bind(target, targetWidth, targetHeight, QuadPipeline.Shader.Present);
            _context->PSSetShaderResources(2, 1, &shaderView);
            _pipeline.Draw(Constants(rect, targetWidth, targetHeight, reduce));
        }
        finally
        {
            _pipeline.Unbind();
            _gpu.UnlockContext();
        }

        return rect;
    }

    public void Dispose() => _pipeline.Dispose();

    private static QuadConstants Constants(PixelRect rect, int targetWidth, int targetHeight, bool reduce)
    {
        float left = (2f * rect.X / targetWidth) - 1f;
        float right = (2f * (rect.X + rect.Width) / targetWidth) - 1f;
        float top = 1f - (2f * rect.Y / targetHeight);
        float bottom = 1f - (2f * (rect.Y + rect.Height) / targetHeight);
        return new QuadConstants
        {
            TopLeft = new(left, top, 0, 0),
            TopRight = new(right, top, 1, 0),
            BottomLeft = new(left, bottom, 0, 1),
            BottomRight = new(right, bottom, 1, 1),
            Present = new(reduce ? 1 : 0, 0, 0, 0),
        };
    }
}
