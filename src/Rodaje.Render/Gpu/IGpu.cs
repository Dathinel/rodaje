using Rodaje.Media.Gpu;
using Rodaje.Render.Shaders;

namespace Rodaje.Render.Gpu;

/// <summary>
/// La capa fina de GPU de Rodaje y Unicomp (D-066): un motor por API (D3D11 en Windows, Vulkan en Linux y macOS). Contrato y
/// reglas de sincronización en <c>docs/igpu.md</c>.
/// </summary>
/// <remarks>
/// Crear recursos se puede desde cualquier hilo. Grabar trabajo, solo dentro de un <see cref="IGpuFrame"/>, de un hilo cada vez.
/// Un fallo del motor es una <see cref="RenderException"/>; si además <see cref="IsLost"/> es verdadero, el dispositivo se perdió y
/// hay que crear otro.
/// </remarks>
public interface IGpu : IDisposable
{
    GpuCaps Caps { get; }

    /// <summary>Perdido (TDR, controlador actualizado, <c>VK_ERROR_DEVICE_LOST</c>): ningún recurso vale ya.</summary>
    bool IsLost { get; }

    GpuTexture CreateTexture(in GpuTextureDesc desc);

    /// <summary>Con contenido inicial: filas de arriba abajo, sin relleno, una rebanada tras otra en 3D (solo el mip 0).</summary>
    GpuTexture CreateTexture(in GpuTextureDesc desc, ReadOnlySpan<byte> initialData);

    GpuBuffer CreateBuffer(in GpuBufferDesc desc, ReadOnlySpan<byte> initialData = default);

    GpuSampler CreateSampler(in GpuSamplerDesc desc);

    /// <summary>Un sombreador del manifiesto de variantes o uno propio con la misma forma.</summary>
    /// <param name="cache">Para los motores que compilan HLSL en ejecución (D3D11); <c>null</c>, la caché del proceso.</param>
    /// <exception cref="RenderException">No compila o el motor no lo admite.</exception>
    GpuShader CreateShader(ShaderVariant variant, ShaderCache? cache = null);

    GpuPipeline CreatePipeline(GpuPipelineDesc desc);

    GpuTimerQuery CreateTimer();

    /// <summary>Destino de lectura reutilizable para una textura de <paramref name="width"/>×<paramref name="height"/>.</summary>
    GpuReadback CreateReadback(int width, int height, GpuFormat format);

    /// <summary>Destino de lectura reutilizable de <paramref name="bytes"/> bytes para un búfer.</summary>
    GpuReadback CreateReadback(int bytes);

    /// <summary>Vistas de superficies del decodificador para <see cref="IGpuFrame.Acquire"/>.</summary>
    /// <param name="time">Reloj de las vistas sin uso; <c>null</c>, el del sistema.</param>
    GpuSurfaceCache CreateSurfaceCache(TimeProvider? time = null);

    /// <summary>Empieza a grabar. Ver «Sincronización» en <c>docs/igpu.md</c>.</summary>
    IGpuFrame BeginFrame();
}

/// <summary>Donde se graba el trabajo de un cuadro. Lo usa un solo hilo; <see cref="IDisposable.Dispose"/> lo envía y lo cierra.</summary>
public interface IGpuFrame : IDisposable
{
    // Subida y copia
    void Write<T>(GpuBuffer buffer, ReadOnlySpan<T> data)
        where T : unmanaged;

    void Write(GpuTexture texture, ReadOnlySpan<byte> pixels, int rowPitch, int depthPitch = 0, int mip = 0);

    void Copy(GpuTexture source, GpuTexture destination);

    /// <summary>Copia <paramref name="region"/> de <paramref name="source"/> a (<paramref name="x"/>, <paramref name="y"/>) del destino, en el mip 0.</summary>
    void Copy(GpuTexture source, in GpuBox region, GpuTexture destination, int x, int y);

    void GenerateMips(GpuTexture texture);

    void Clear(GpuTexture target, in GpuColor color);

    /// <summary>
    /// Pone <paramref name="value"/> en un destino de enteros (<see cref="GpuFormat.R32Uint"/>) o en algo con escritura desde
    /// un shader (todo el búfer o la textura). En un destino de color sin <see cref="GpuTextureUsage.Storage"/>, el valor es exacto
    /// hasta 2^24.
    /// </summary>
    void ClearUint(GpuResource target, uint value);

    void ClearDepth(GpuTexture depth, float value = 1);

    /// <summary>
    /// Los planos de una superficie del decodificador, sin copiarla, válidos hasta que el cuadro se cierra (regla 2 de
    /// <c>docs/igpu.md</c>). Las vistas se guardan en <paramref name="cache"/>.
    /// </summary>
    /// <exception cref="ArgumentException">La superficie no es de este motor.</exception>
    GpuDecoderPlanes Acquire(GpuSurfaceCache cache, IDecoderSurface surface);

    // Dibujo y cómputo
    void Draw(GpuPipeline pipeline, in GpuTargets targets, in GpuBindings bindings, in GpuViewport viewport, in GpuDrawCall draw);

    void Dispatch(GpuPipeline pipeline, in GpuBindings bindings, int groupsX, int groupsY, int groupsZ = 1);

    /// <summary>Resuelve un destino con MSAA en uno de una muestra del mismo formato (<i>Raster</i>).</summary>
    void Resolve(GpuTexture multisampled, GpuTexture destination);

    // Lectura y medida
    /// <summary>Graba la copia de <paramref name="region"/> a <paramref name="destination"/>; los bytes valen cuando el cuadro terminó.</summary>
    void Copy(GpuTexture source, in GpuBox region, GpuReadback destination);

    void Copy(GpuBuffer source, int offset, int bytes, GpuReadback destination);

    void BeginTimer(GpuTimerQuery timer);

    void EndTimer(GpuTimerQuery timer);

    /// <summary>Envía lo grabado hasta aquí y devuelve su valla; se puede seguir grabando después.</summary>
    GpuFence Submit();

    /// <summary>Envía lo grabado hasta aquí sin valla (para quien no va a esperar: no crea nada que liberar).</summary>
    void Flush();
}
