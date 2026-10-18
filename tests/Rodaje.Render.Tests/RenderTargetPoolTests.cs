using Rodaje.Media.Gpu;
using Rodaje.TestMedia;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.D3D11;

namespace Rodaje.Render.Tests;

/// <summary>
/// Un intermedio nuevo empieza en cero aunque su memoria tuviera NaN. Los efectos dibujan solo en lo que pidieron, pero el
/// filtro bilineal del borde lee el texel siguiente con peso 0 y 0 × NaN es NaN: con WARP y memoria sin limpiar la vista
/// previa y la exportación diferían en las últimas filas (CI, PR #427).
/// </summary>
public sealed unsafe class RenderTargetPoolTests
{
    [GpuFact]
    public void A_new_intermediate_starts_at_zero_even_over_memory_that_held_nan()
    {
        using GpuDevice gpu = GpuDevice.Create();
        var device = (ID3D11Device*)gpu.NativeDevice;
        gpu.LockContext();
        try
        {
            var context = (ID3D11DeviceContext*)gpu.NativeContext;
            using var pool = new RenderTargetPool(device, context, RenderTargetPool.MinCapBytes);
            float* nan = stackalloc float[4] { float.NaN, float.NaN, float.NaN, float.NaN };
            int poisoned = 0;
            for (int round = 0; round < 8; round++)
            {
                RenderTarget target = pool.Rent(500, 280, LayerRenderer.IntermediateFormat)!;
                Assert.Equal(0, NonZeroHalves(device, context, target));
                context->ClearRenderTargetView(target.Rtv, nan);
                poisoned += NonZeroHalves(device, context, target) > 0 ? 1 : 0;
                pool.Return(target);
                pool.Trim();
                context->Flush();
            }

            Assert.Equal(8, poisoned);
        }
        finally
        {
            gpu.UnlockContext();
        }
    }

    private static int NonZeroHalves(ID3D11Device* device, ID3D11DeviceContext* context, RenderTarget target)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)target.Width,
            Height = (uint)target.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = target.Format,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
        };
        ID3D11Texture2D* staging;
        Assert.True(device->CreateTexture2D(&desc, null, &staging).SUCCEEDED);
        try
        {
            context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)target.Texture);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Assert.True(context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped).SUCCEEDED);
            int count = 0;
            for (int y = 0; y < target.Height; y++)
            {
                var row = new ReadOnlySpan<ushort>((byte*)mapped.pData + (y * mapped.RowPitch), target.Width * 4);
                foreach (ushort half in row)
                {
                    count += half != 0 ? 1 : 0;
                }
            }

            context->Unmap((ID3D11Resource*)staging, 0);
            return count;
        }
        finally
        {
            staging->Release();
        }
    }
}
