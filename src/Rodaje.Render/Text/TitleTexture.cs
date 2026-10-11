using TerraFX.Interop.DirectX;

namespace Rodaje.Render.Text;

/// <summary>
/// Un título rasterizado: textura BGRA premultiplicada centrada en el bloque de texto, con margen para el borde y la
/// sombra. <see cref="LocalWidth"/>×<see cref="LocalHeight"/> es lo que mide en píxeles de la secuencia a escala 1.
/// </summary>
public sealed unsafe class TitleTexture
{
    internal TitleTexture(RenderTarget target, double localWidth, double localHeight, double scale, bool fontMissing, bool downscaled)
    {
        Target = target;
        LocalWidth = localWidth;
        LocalHeight = localHeight;
        Scale = scale;
        FontMissing = fontMissing;
        Downscaled = downscaled;
    }

    /// <summary>Ancho de la textura en píxeles.</summary>
    public int Width => Target.Width;

    public int Height => Target.Height;

    public double LocalWidth { get; }

    public double LocalHeight { get; }

    /// <summary>Píxeles de la textura por píxel de la secuencia.</summary>
    public double Scale { get; }

    /// <summary>La fuente pedida no estaba: se dibujó con un respaldo.</summary>
    public bool FontMissing { get; }

    /// <summary>No cabía en el tope de memoria a su escala y se rasterizó más chica.</summary>
    public bool Downscaled { get; }

    public long Bytes => Target.Bytes;

    /// <summary><c>ID3D11ShaderResourceView*</c> de la textura; vale mientras el rasterizador la tenga en caché.</summary>
    public nint ShaderResourceView => (nint)Target.Srv;

    internal RenderTarget Target { get; }

    internal ID3D11Texture2D* Texture => Target.Texture;
}
