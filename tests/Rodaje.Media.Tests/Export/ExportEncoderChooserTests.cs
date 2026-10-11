using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

public sealed class ExportEncoderChooserTests
{
    private static ExportEncoderInfo[] Only(params string[] ids) => [.. ids.Select(id => ExportEncoderCatalog.Find(id)!)];

    [Fact]
    public void Nothing_available_gives_no_choice()
    {
        Assert.Null(ExportEncoderChooser.ChooseDefault([]));
        Assert.Null(ExportEncoderChooser.Choose([], ExportCodec.H264));
    }

    [Fact]
    public void With_a_gpu_the_default_is_hardware_h264_even_if_software_av1_exists()
    {
        ExportEncoderChoice? choice = ExportEncoderChooser.ChooseDefault(Only("libsvtav1", "h264_nvenc", "av1_nvenc"));

        Assert.Equal("h264_nvenc", choice!.Encoder.Id);
        Assert.False(choice.Substituted);
        Assert.Null(choice.Note);
    }

    [Fact]
    public void With_a_gpu_without_h264_the_default_is_hevc_then_av1()
    {
        Assert.Equal("hevc_amf", ExportEncoderChooser.ChooseDefault(Only("av1_amf", "hevc_amf"))!.Encoder.Id);
        Assert.Equal("av1_qsv", ExportEncoderChooser.ChooseDefault(Only("av1_qsv", "libsvtav1"))!.Encoder.Id);
    }

    [Fact]
    public void Among_gpus_the_catalog_order_decides_the_vendor()
    {
        Assert.Equal("h264_nvenc", ExportEncoderChooser.ChooseDefault(Only("h264_mf", "h264_qsv", "h264_nvenc"))!.Encoder.Id);
        Assert.Equal("h264_qsv", ExportEncoderChooser.ChooseDefault(Only("h264_mf", "h264_qsv"))!.Encoder.Id);
    }

    [Fact]
    public void The_order_in_which_the_list_arrives_does_not_matter()
    {
        ExportEncoderInfo[] list = Only("libvpx-vp9", "libopenh264", "libaom-av1", "libsvtav1");

        Assert.Equal("libsvtav1", ExportEncoderChooser.ChooseDefault(list)!.Encoder.Id);
        Assert.Equal("libsvtav1", ExportEncoderChooser.ChooseDefault(list.Reverse())!.Encoder.Id);
    }

    [Fact]
    public void Without_a_gpu_the_default_is_software_av1_before_hevc_and_h264()
    {
        Assert.Equal("libsvtav1", ExportEncoderChooser.ChooseDefault(Only("libopenh264", "libkvazaar", "libsvtav1"))!.Encoder.Id);
        Assert.Equal("libkvazaar", ExportEncoderChooser.ChooseDefault(Only("libopenh264", "libkvazaar"))!.Encoder.Id);
    }

    [Fact]
    public void Openh264_alone_is_chosen_only_as_a_last_resort_with_a_note()
    {
        ExportEncoderChoice? choice = ExportEncoderChooser.ChooseDefault(Only("libopenh264"));

        Assert.Equal("libopenh264", choice!.Encoder.Id);
        Assert.False(string.IsNullOrEmpty(choice.Note));
    }

    [Fact]
    public void Requested_h264_with_a_gpu_uses_it()
    {
        ExportEncoderChoice? choice = ExportEncoderChooser.Choose(Only("h264_mf", "libsvtav1"), ExportCodec.H264);

        Assert.Equal("h264_mf", choice!.Encoder.Id);
        Assert.False(choice.Substituted);
    }

    [Fact]
    public void Requested_h264_without_a_gpu_falls_back_to_av1_by_cpu_and_says_so()
    {
        ExportEncoderChoice? choice = ExportEncoderChooser.Choose(Only("libopenh264", "libkvazaar", "libsvtav1"), ExportCodec.H264);

        Assert.Equal("libsvtav1", choice!.Encoder.Id);
        Assert.True(choice.Substituted);
        Assert.Equal(ExportCodec.H264, choice.Requested);
        Assert.Contains("H.264", choice.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Requested_h264_without_a_gpu_or_av1_falls_back_to_hevc_then_vp9()
    {
        Assert.Equal("libkvazaar", ExportEncoderChooser.Choose(Only("libopenh264", "libkvazaar", "libvpx-vp9"), ExportCodec.H264)!.Encoder.Id);
        Assert.Equal("libvpx-vp9", ExportEncoderChooser.Choose(Only("libopenh264", "libvpx-vp9"), ExportCodec.H264)!.Encoder.Id);
    }

    [Fact]
    public void Requested_h264_with_only_openh264_gets_it_with_a_note()
    {
        ExportEncoderChoice? choice = ExportEncoderChooser.Choose(Only("libopenh264"), ExportCodec.H264);

        Assert.Equal("libopenh264", choice!.Encoder.Id);
        Assert.False(choice.Substituted);
        Assert.False(string.IsNullOrEmpty(choice.Note));
    }

    [Fact]
    public void Requested_codecs_other_than_h264_are_never_substituted()
    {
        Assert.Null(ExportEncoderChooser.Choose(Only("libsvtav1", "libopenh264"), ExportCodec.Hevc));
        Assert.Equal("libkvazaar", ExportEncoderChooser.Choose(Only("libkvazaar"), ExportCodec.Hevc)!.Encoder.Id);
        Assert.Equal("hevc_nvenc", ExportEncoderChooser.Choose(Only("libkvazaar", "hevc_nvenc"), ExportCodec.Hevc)!.Encoder.Id);
    }

    [Fact]
    public void Duplicates_in_the_input_are_ignored()
    {
        ExportEncoderInfo[] list = [.. Only("libsvtav1"), .. Only("libsvtav1")];

        Assert.Equal("libsvtav1", ExportEncoderChooser.ChooseDefault(list)!.Encoder.Id);
    }
}
