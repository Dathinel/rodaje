using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rodaje.Playback.Composition;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using Rodaje.Render.Shaders;

namespace Rodaje.Render.Effects;

/// <summary>Constantes de los efectos, en el orden del <c>cbuffer Effects</c> de <see cref="EffectShaders"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
internal struct EffectConstants
{
    public Float4 Grade;
    public Float4 Balance;
    public Float4 Tones;
    public Float4 Vignette;
    public Float4 BlendInfo;
    public Float4 MapA;
    public Float4 MapB;
    public Float4 Kernel;
    public Float4 Mix0;
    public Float4 Mix1;
    public Float4 Canvas;
    public Float4 Key0;
    public Float4 Key1;
    public Float4 Lift;
    public Float4 GammaExp;
    public Float4 Gain;
    public Float4 Extra;
    public Float4 Skin;
    public Float4 Vignette2;
    public Float4 Qual0;
    public Float4 Qual1;
    public Float4 Qual2;
    public Float4 Qual3;
    public Float4 Fx0;
    public Float4 Fx1;
    public Float4 Fx2;
}

/// <summary>
/// Shaders, constantes y mezclas de los efectos. Los shaders se crean en segundo plano (de la caché en disco o compilándolos);
/// mientras uno no está, <see cref="TryGetShader"/> devuelve <c>null</c> y el cuadro sigue sin él.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
internal sealed class EffectPipeline : IDisposable
{
    // Con fuente premultiplicada S = Cs·a y fondo opaco Cb (fórmulas W3C con alfa):
    // normal  Cb·(1 − a) + S;  suma  Cb + S;  multiplicar  Cb·(1 − a) + S·Cb;  trama  Cb + S·(1 − Cb).
    private static readonly GpuBlend s_over = Blend(GpuBlendFactor.One, GpuBlendFactor.InverseSourceAlpha);
    private static readonly GpuBlend s_add = Blend(GpuBlendFactor.One, GpuBlendFactor.One);
    private static readonly GpuBlend s_accumulate = Blend(GpuBlendFactor.One, GpuBlendFactor.One, GpuBlendFactor.One, GpuBlendFactor.One);
    private static readonly GpuBlend s_multiply = Blend(GpuBlendFactor.DestinationColor, GpuBlendFactor.InverseSourceAlpha);
    private static readonly GpuBlend s_screen = Blend(GpuBlendFactor.InverseDestinationColor, GpuBlendFactor.One);
    private static readonly GpuBlend s_maskAdd = Blend(GpuBlendFactor.InverseDestinationColor, GpuBlendFactor.One,
        GpuBlendFactor.InverseDestinationAlpha, GpuBlendFactor.One);
    private static readonly GpuBlend s_maskSubtract = Blend(GpuBlendFactor.Zero, GpuBlendFactor.InverseSourceColor,
        GpuBlendFactor.Zero, GpuBlendFactor.InverseSourceAlpha);
    private static readonly GpuBlend s_maskMultiply = Blend(GpuBlendFactor.Zero, GpuBlendFactor.SourceColor,
        GpuBlendFactor.Zero, GpuBlendFactor.SourceAlpha);
    private static readonly GpuBlend s_maskDifference = Blend(GpuBlendFactor.InverseDestinationColor, GpuBlendFactor.InverseSourceColor,
        GpuBlendFactor.InverseDestinationAlpha, GpuBlendFactor.InverseSourceAlpha);

    private readonly QuadPipeline _quad;
    private readonly GpuShader?[] _shaders = new GpuShader?[EffectShaders.All.Length];
    private readonly Lock _gate = new();
    private readonly GpuBuffer _constants;
    private bool _disposed;

    public unsafe EffectPipeline(QuadPipeline quad, ShaderCache cache)
    {
        _quad = quad;
        D3D11Gpu engine = quad.Engine;
        _constants = engine.CreateBuffer(new GpuBufferDesc(sizeof(EffectConstants), GpuBufferKind.Constant));

        // El hilo de fondo retiene el dispositivo hasta terminar, aunque el compositor se cierre antes.
        engine.NativeDevice->AddRef();
        Ready = Task.Run(() =>
        {
            try
            {
                CreateShaders(engine, cache);
            }
            finally
            {
                engine.NativeDevice->Release();
            }
        });
    }

    /// <summary>Termina cuando todas las variantes están creadas (o falló alguna: la excepción queda aquí).</summary>
    public Task Ready { get; }

    /// <summary>Normal, suma, multiplicar y trama se resuelven con el estado de mezcla; el resto, en el shader.</summary>
    public static bool IsShaderBlend(BlendMode mode) =>
        mode is not (BlendMode.Normal or BlendMode.Add or BlendMode.Multiply or BlendMode.Screen);

    public GpuShader? TryGetShader(EffectShader shader, int flags) => Volatile.Read(ref _shaders[EffectShaders.IndexOf(shader, flags)]);

    /// <summary>Mezcla para un valor premultiplicado; los modos por shader usan "over".</summary>
    public static GpuBlend BlendState(BlendMode mode) => mode switch
    {
        BlendMode.Add => s_add,
        BlendMode.Multiply => s_multiply,
        BlendMode.Screen => s_screen,
        _ => s_over,
    };

    /// <summary>Suma en los cuatro canales: acumula las muestras premultiplicadas del desenfoque de movimiento.</summary>
    public static GpuBlend Accumulate => s_accumulate;

    /// <summary>
    /// Cómo combina una máscara su cobertura <c>m</c> con la textura de máscaras <c>a</c>, en los cuatro canales: suma
    /// <c>m·(1 − a) + a</c>, resta <c>a·(1 − m)</c>, intersección <c>a·m</c> y diferencia <c>m·(1 − a) + a·(1 − m)</c>.
    /// </summary>
    public static GpuBlend MaskBlend(Rodaje.Core.Effects.MaskMode mode) => mode switch
    {
        Rodaje.Core.Effects.MaskMode.Add => s_maskAdd,
        Rodaje.Core.Effects.MaskMode.Subtract => s_maskSubtract,
        Rodaje.Core.Effects.MaskMode.Difference => s_maskDifference,
        _ => s_maskMultiply,
    };

    public void BindConstants() => _quad.SetConstants(1, _constants);

    public void Set(in EffectConstants constants) =>
        _quad.CurrentFrame.Write(_constants, MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in constants), 1));

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            for (int i = 0; i < _shaders.Length; i++)
            {
                _shaders[i]?.Dispose();
                _shaders[i] = null;
            }
        }

        _constants.Dispose();
    }

    private void CreateShaders(D3D11Gpu engine, ShaderCache cache)
    {
        for (int i = 0; i < EffectShaders.All.Length; i++)
        {
            if (Volatile.Read(ref _disposed))
            {
                return;
            }

            GpuShader shader = engine.CreateShader(EffectShaders.All[i], cache);
            lock (_gate)
            {
                if (_disposed)
                {
                    shader.Dispose();
                    return;
                }

                Volatile.Write(ref _shaders[i], shader);
            }
        }
    }

    private static GpuBlend Blend(GpuBlendFactor source, GpuBlendFactor destination,
        GpuBlendFactor sourceAlpha = GpuBlendFactor.One, GpuBlendFactor destinationAlpha = GpuBlendFactor.InverseSourceAlpha) =>
        new(source, destination, sourceAlpha, destinationAlpha);
}
