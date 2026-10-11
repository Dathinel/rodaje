using Rodaje.Core.Timing;
using Rodaje.Media.Export;
using Rodaje.Render.Export;

namespace Rodaje.Render.Tests.Export;

public sealed class ExportGeometryTests
{
    [Fact]
    public void Same_shape_fills_the_frame_whatever_the_fit()
    {
        foreach (ExportFit fit in Enum.GetValues<ExportFit>())
        {
            Assert.Equal(new ContentRect(0, 0, 1280, 720), ExportGeometry.Place(1920, 1080, 1280, 720, fit));
        }
    }

    [Fact]
    public void Landscape_in_portrait_gets_bars_on_even_rows()
    {
        // 16:9 en 1080×1920: 1080×607,5 → 608 (par) centrado en una fila par.
        ContentRect rect = ExportGeometry.Place(1920, 1080, 1080, 1920, ExportFit.Bars, evenX: true, evenY: true);
        Assert.Equal(new ContentRect(0, 656, 1080, 608), rect);
        Assert.Equal(0, rect.Y % 2);
    }

    [Fact]
    public void Portrait_in_landscape_gets_side_bars()
    {
        ContentRect rect = ExportGeometry.Place(1080, 1920, 1920, 1080, ExportFit.Bars, evenX: true, evenY: true);
        Assert.Equal(new ContentRect(656, 0, 608, 1080), rect);
    }

    [Fact]
    public void Crop_overflows_both_sides_evenly()
    {
        // 16:9 recortado a 1080×1920: la composición mide 3413×1920 y sobra (3413 − 1080)/2 a cada lado.
        ContentRect rect = ExportGeometry.Place(1920, 1080, 1080, 1920, ExportFit.Crop);
        Assert.Equal(new ContentRect(-1166, 0, 3413, 1920), rect);
    }

    [Fact]
    public void Stretch_always_fills()
    {
        Assert.Equal(new ContentRect(0, 0, 1080, 1920), ExportGeometry.Place(1920, 1080, 1080, 1920, ExportFit.Stretch));
    }

    [Fact]
    public void Exact_half_reads_mip_one_without_a_scale_pass()
    {
        var rect = new ContentRect(0, 0, 1920, 1080);
        ScalePlan plan = ExportGeometry.Plan(3840, 2160, 1920, 1080, rect, ExportScaleFilter.Auto, maxLevel: 11);
        Assert.Equal(1, plan.Level);
        Assert.Equal(ScaleKernel.Point, plan.Kernel);
        Assert.True(plan.PassThrough);
    }

    [Fact]
    public void Quarter_reads_mip_two()
    {
        ScalePlan plan = ExportGeometry.Plan(3840, 2160, 960, 540, new ContentRect(0, 0, 960, 540), ExportScaleFilter.Auto, 11);
        Assert.Equal(2, plan.Level);
        Assert.True(plan.PassThrough);
    }

    [Fact]
    public void Same_size_passes_through_level_zero()
    {
        ScalePlan plan = ExportGeometry.Plan(1920, 1080, 1920, 1080, new ContentRect(0, 0, 1920, 1080), ExportScaleFilter.Auto, 11);
        Assert.Equal(new ScalePlan(new ContentRect(0, 0, 1920, 1080), 0, ScaleKernel.Point, 1, 1, true), plan);
    }

    [Fact]
    public void Third_reads_mip_one_and_widens_catmull_rom()
    {
        // 4K → 720p: un tercio. Se lee el mip 1 (1920) y se reduce 2/3 con el núcleo 1,5 veces más ancho.
        ScalePlan plan = ExportGeometry.Plan(3840, 2160, 1280, 720, new ContentRect(0, 0, 1280, 720), ExportScaleFilter.Auto, 11);
        Assert.Equal(1, plan.Level);
        Assert.Equal(ScaleKernel.CatmullRom, plan.Kernel);
        Assert.Equal(1.5f, plan.WidenX, 4);
        Assert.Equal(1.5f, plan.WidenY, 4);
        Assert.False(plan.PassThrough);
    }

    [Fact]
    public void Upscale_uses_catmull_rom_without_widening()
    {
        ScalePlan plan = ExportGeometry.Plan(1280, 720, 1920, 1080, new ContentRect(0, 0, 1920, 1080), ExportScaleFilter.Auto, 10);
        Assert.Equal((0, ScaleKernel.CatmullRom, 1f, 1f), (plan.Level, plan.Kernel, plan.WidenX, plan.WidenY));
    }

    [Fact]
    public void Explicit_filters_read_level_zero()
    {
        var rect = new ContentRect(0, 0, 1920, 1080);
        Assert.Equal((0, ScaleKernel.Lanczos3, 2f), Summary(ExportGeometry.Plan(3840, 2160, 1920, 1080, rect, ExportScaleFilter.Lanczos, 11)));
        Assert.Equal((0, ScaleKernel.Bilinear, 2f), Summary(ExportGeometry.Plan(3840, 2160, 1920, 1080, rect, ExportScaleFilter.Bilinear, 11)));
        Assert.Equal((0, ScaleKernel.CatmullRom, 2f), Summary(ExportGeometry.Plan(3840, 2160, 1920, 1080, rect, ExportScaleFilter.CatmullRom, 11)));

        static (int, ScaleKernel, float) Summary(ScalePlan plan) => (plan.Level, plan.Kernel, plan.WidenX);
    }

    [Fact]
    public void Odd_source_does_not_use_mips()
    {
        ScalePlan plan = ExportGeometry.Plan(1921, 1080, 960, 540, new ContentRect(0, 0, 960, 540), ExportScaleFilter.Auto, 11);
        Assert.Equal(0, plan.Level);
    }

    [Fact]
    public void Bars_with_a_half_size_picture_still_read_mip_one()
    {
        // 3840×2160 en 1920×1920 con barras: la imagen mide 1920×1080 (la mitad exacta) pero no llena el cuadro.
        ContentRect rect = ExportGeometry.Place(3840, 2160, 1920, 1920, ExportFit.Bars, true, true);
        ScalePlan plan = ExportGeometry.Plan(3840, 2160, 1920, 1920, rect, ExportScaleFilter.Auto, 11);
        Assert.Equal((1, ScaleKernel.Point, false), (plan.Level, plan.Kernel, plan.PassThrough));
    }

    [Fact]
    public void Layout_of_nv12_1080p_is_tight()
    {
        var layout = new ExportLayout(new ExportVideoFormat(1920, 1080, new Rational(30, 1), ExportPixelFormat.Nv12, ExportColor.Bt709Limited));
        Assert.True(layout.IsTight);
        Assert.Equal(2, layout.Planes.Count);
        Assert.Equal(1920 * 1080, layout.Planes[1].Offset);
        Assert.Equal(1920 * 1080 * 3 / 2, layout.TotalBytes);
    }

    [Fact]
    public void Layout_pads_rows_to_whole_words()
    {
        var format = new ExportVideoFormat(61, 35, new Rational(30, 1), ExportPixelFormat.Rgb24, ExportColor.Srgb);
        var layout = new ExportLayout(format);
        ExportPlane plane = Assert.Single(layout.Planes);
        Assert.Equal(183, plane.RowBytes);
        Assert.Equal(16 * 12, plane.Pitch);
        Assert.False(layout.IsTight);
        Assert.Equal(0, layout.TotalBytes % 16);
    }

    [Theory]
    [InlineData(ExportPixelFormat.Yuv420P10, 3)]
    [InlineData(ExportPixelFormat.Yuva444P10, 4)]
    [InlineData(ExportPixelFormat.Rgba64, 1)]
    public void Every_plane_starts_aligned(ExportPixelFormat pixels, int count)
    {
        var layout = new ExportLayout(new ExportVideoFormat(1918, 1078, new Rational(25, 1), pixels, ExportColor.For(1918, 1078, pixels)));
        Assert.Equal(count, layout.Planes.Count);
        Assert.All(layout.Planes, p => Assert.Equal(0, p.Offset % 16));
        Assert.All(layout.Planes, p => Assert.True(p.Pitch >= p.RowBytes && p.Pitch % 4 == 0));
    }
}
