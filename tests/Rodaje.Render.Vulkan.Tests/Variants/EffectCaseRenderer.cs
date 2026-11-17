using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using TerraFX.Interop.DirectX;

namespace Rodaje.Render.Tests.Variants;

internal sealed class EffectCaseRenderer : IDisposable
{
    private readonly IGpu _gpu;
    private readonly List<IDisposable> _resources = [];
    private readonly Dictionary<string, GpuTexture> _textures = new(StringComparer.Ordinal);
    private readonly Dictionary<int, GpuPipeline> _pipelines = [];
    private readonly GpuShader _vertex;
    private readonly GpuBuffer _quad;
    private readonly GpuBuffer _effects;
    private readonly GpuSampler _sampler;
    private readonly GpuTexture _target;
    private readonly GpuReadback _readback;

    public EffectCaseRenderer(IGpu gpu)
    {
        _gpu = gpu;
        try
        {
            _vertex = Own(gpu.CreateShader(CaseVariants.Vertex));
            _quad = Own(gpu.CreateBuffer(new GpuBufferDesc(272, GpuBufferKind.Constant)));
            _effects = Own(gpu.CreateBuffer(new GpuBufferDesc(416, GpuBufferKind.Constant)));
            _sampler = Own(gpu.CreateSampler(new GpuSamplerDesc(GpuFilter.Linear, GpuAddressMode.Clamp)));
            _target = Own(gpu.CreateTexture(new GpuTextureDesc(Cases.Size, Cases.Size, GpuFormat.Rgba16Float, GpuTextureUsage.RenderTarget)));
            _readback = Own(gpu.CreateReadback(Cases.Size, Cases.Size, GpuFormat.Rgba16Float));
            foreach (TextureSpec spec in Cases.TextureSet())
            {
                byte[] data = Patterns.Bytes(spec);
                Assert.Equal(spec.Hash, Patterns.Hash(data));
                GpuFormat format = spec.Format switch
                {
                    "r32f" => GpuFormat.R32Float,
                    "rgba32f" => GpuFormat.Rgba32Float,
                    _ => GpuFormat.Rgba16Float,
                };
                if (spec.Format is "r16f" or "rg16f")
                {
                    // IGpu no ofrece R16F/RG16F: ampliar conserva exactamente los canales que lee el shader.
                    ReadOnlySpan<Half> source = MemoryMarshal.Cast<byte, Half>(data);
                    int channels = Patterns.Components(spec.Format);
                    var expanded = new Half[source.Length / channels * 4];
                    for (int i = 0; i < source.Length / channels; i++)
                    {
                        source.Slice(i * channels, channels).CopyTo(expanded.AsSpan(i * 4));
                        expanded[(i * 4) + 3] = (Half)1;
                    }

                    data = MemoryMarshal.AsBytes(expanded.AsSpan()).ToArray();
                }

                GpuTexture texture = Own(gpu.CreateTexture(new GpuTextureDesc(spec.Width, spec.Height, format,
                    GpuTextureUsage.Sampled, Depth: spec.Depth), data));
                if (spec.Dimension == "2d-array" && OperatingSystem.IsWindowsVersionAtLeast(7) && gpu is D3D11Gpu)
                {
                    UseArrayView(texture);
                }

                _textures.Add(spec.Name, texture);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private T Own<T>(T resource) where T : IDisposable
    {
        _resources.Add(resource);
        return resource;
    }

    public float[] Render(CaseSpec spec)
    {
        if (!_pipelines.TryGetValue(spec.Variant, out GpuPipeline? pipeline))
        {
            GpuShader pixel = Own(_gpu.CreateShader(CaseVariants.All[spec.Variant]));
            pipeline = Own(_gpu.CreatePipeline(new GpuPipelineDesc { Vertex = _vertex, Pixel = pixel }));
            _pipelines.Add(spec.Variant, pipeline);
        }

        var bindings = new GpuBindings();
        bindings.SetConstants(0, _quad);
        bindings.SetConstants(1, _effects);
        bindings.SetSampler(0, _sampler);
        foreach ((string register, string name) in spec.Textures)
        {
            bindings.SetResource(int.Parse(register.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture), _textures[name]);
        }

        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            frame.Write<float>(_quad, spec.Quad);
            frame.Write<float>(_effects, spec.Effects);
            frame.Clear(_target, GpuColor.TransparentBlack);
            frame.Draw(pipeline, new GpuTargets(_target), bindings, GpuViewport.Of(Cases.Size, Cases.Size), new GpuDrawCall(4));
            frame.Copy(_target, new GpuBox(0, 0, Cases.Size, Cases.Size), _readback);
        }

        var halves = new Half[Cases.Size * Cases.Size * 4];
        _readback.Read(MemoryMarshal.AsBytes(halves.AsSpan()));
        return Array.ConvertAll(halves, static h => (float)h);
    }

    [SupportedOSPlatform("windows7.0")]
    private static unsafe void UseArrayView(GpuTexture texture)
    {
        // D3D11 crea una vista 2D; el acceso queda en pruebas porque el contrato no expone vistas de arreglo.
        FieldInfo field = texture.GetType().GetField("_srv", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(texture.GetType().FullName, "_srv");
        var original = (ID3D11ShaderResourceView*)Pointer.Unbox(field.GetValue(texture)!);
        ID3D11Resource* resource;
        ID3D11Device* device;
        original->GetResource(&resource);
        original->GetDevice(&device);
        try
        {
            D3D11_SHADER_RESOURCE_VIEW_DESC desc;
            original->GetDesc(&desc);
            desc.ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE2DARRAY;
            desc.Texture2DArray.MostDetailedMip = 0;
            desc.Texture2DArray.MipLevels = 1;
            desc.Texture2DArray.FirstArraySlice = 0;
            desc.Texture2DArray.ArraySize = 1;
            ID3D11ShaderResourceView* view;
            Marshal.ThrowExceptionForHR(device->CreateShaderResourceView(resource, &desc, &view).Value);
            try
            {
                field.SetValue(texture, Pointer.Box(view, field.FieldType));
            }
            catch
            {
                view->Release();
                throw;
            }

            original->Release();
        }
        finally
        {
            device->Release();
            resource->Release();
        }
    }

    public void Dispose()
    {
        for (int i = _resources.Count - 1; i >= 0; i--)
        {
            _resources[i].Dispose();
        }

        _resources.Clear();
    }
}

