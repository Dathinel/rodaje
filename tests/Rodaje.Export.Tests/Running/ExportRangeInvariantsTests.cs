using Rodaje.Core.Timing;
using Rodaje.Export.Running;
using Rodaje.Media.Export;

namespace Rodaje.Export.Tests.Running;

public sealed class ExportRangeInvariantsTests
{
    [Theory]
    [InlineData(0, 1, 0, 1)]
    [InlineData(29, 30, 29, 1)]
    [InlineData(0, 300, 0, 30)]
    [InlineData(29, 300, 29, 1)]
    public void Single_frame_and_oversized_ranges_have_exact_frame_counts(int start, int end, long first, long count)
    {
        ExportPlan plan = Plan(start, end);
        Assert.Equal(first, plan.FirstFrame);
        Assert.Equal(count, plan.FrameCount);
        Assert.Equal(F(first), plan.Start);
        Assert.Equal(F(first + count), plan.End);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(15, 15)]
    [InlineData(30, 31)]
    [InlineData(31, 300)]
    [InlineData(20, 10)]
    [InlineData(-1, 1)]
    public void Empty_reversed_negative_and_outside_ranges_report_invalid_settings(int start, int end)
    {
        ExportException error = Assert.Throws<ExportException>(() => Plan(start, end));
        Assert.Equal(ExportErrorCode.InvalidSettings, error.Code);
        Assert.Equal("El rango que se exporta está vacío.", error.Message);
    }

    private static ExportPlan Plan(int start, int end) => ExportPlan.Create(new ExportRequest(
        ExportPlanTests.Project(F(30)), "sec", "salida ñ.mp4",
        new ExportSettings { Video = new VideoExportSettings { Codec = ExportCodec.H264, EncoderId = "h264_nvenc" } },
        new ExportRange(F(start), F(end))), ExportEncoderCatalog.All);

    private static Time F(long frame) => Time.FromFrames(frame, Rational.Fps30);
}
