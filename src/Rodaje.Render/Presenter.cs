using Rodaje.Core.Model;
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

    /// <summary>
    /// El búfer de destino es scRGB (<c>R16G16B16A16_FLOAT</c>, BT.709 lineal con 1,0 = 80 nits): la composición se pasa a esa escala con el
    /// blanco en 203 nits. Si no, una composición HDR (<see cref="Compositor.ColorSpace"/>) sale en SDR BT.709 recortando lo que pasa de 203 nits.
    /// </summary>
    public bool ScRgb { get; set; }

    /// <summary>El color de alrededor en el búfer: tal cual o, en scRGB, a lineal con el blanco en 203 nits, como una capa SDR en <c>PresentHdr</c>.</summary>
    internal static (float R, float G, float B) SurroundFor((float R, float G, float B) color, bool scRgb)
    {
        if (!scRgb)
        {
            return color;
        }

        static float Linear(float x) => (x < 0.081f ? x / 4.5f : MathF.Pow((x + 0.099f) / 1.099f, 1 / 0.45f)) * (203f / 80);
        return (Linear(color.R), Linear(color.G), Linear(color.B));
    }

    /// <summary>Qué hace <c>PsPresent</c> con la composición (<c>present.w</c>): 0 la copia; 1 HDR → SDR; 2 HDR → scRGB; 3 SDR → scRGB.</summary>
    internal static int ModeFor(bool hdrComposition, bool scRgb) => hdrComposition ? (scRgb ? 2 : 1) : (scRgb ? 3 : 0);

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
            (float r, float g, float b) = SurroundFor(Surround, ScRgb);
            float* surround = stackalloc float[] { r, g, b, 1f };
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
            _pipeline.Draw(Constants(rect, targetWidth, targetHeight, reduce, ModeFor(source.ColorSpace != SequenceColorSpace.Sdr709, ScRgb)));
        }
        finally
        {
            _pipeline.Unbind();
            _gpu.UnlockContext();
        }

        return rect;
    }

    public void Dispose() => _pipeline.Dispose();

    internal static QuadConstants Constants(PixelRect rect, int targetWidth, int targetHeight, bool reduce, int mode = 0)
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
            Present = new(reduce ? 1 : 0, 0, 0, mode),
        };
    }
}
