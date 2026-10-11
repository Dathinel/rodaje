using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

public sealed class ExportEncoderChooserTests
{
    private static EncoderSpec[] Of(params string[] ids) =>
        [.. ids.Select(id => ExportEncoderCatalog.Find(id) ?? throw new ArgumentException($"{id} no está en el catálogo."))];

    [Fact]
    public void Nothing_available_gives_no_choice()
    {
        Assert.Null(ExportEncoderChooser.ChooseDefault([]));
        Assert.Null(ExportEncoderChooser.ChooseDefaultAudio([]));
        Assert.Null(ExportEncoderChooser.Choose([], ExportCodec.H264));
    }

    [Fact]
    public void With_a_gpu_the_default_is_hardware_h264_even_if_software_exists()
    {
        ExportEncoderChoice choice = ExportEncoderChooser.ChooseDefault(Of("libsvtav1", "h264_mf", "h264_nvenc"))!;

        Assert.Equal("h264_nvenc", choice.Encoder.Id);
        Assert.Null(choice.Note);
        Assert.False(choice.Substituted);
    }

    [Fact]
    public void With_a_gpu_without_h264_the_default_is_hevc_then_av1()
    {
        Assert.Equal("hevc_nvenc", ExportEncoderChooser.ChooseDefault(Of("av1_nvenc", "hevc_nvenc", "h264_mf"))!.Encoder.Id);
        Assert.Equal("av1_nvenc", ExportEncoderChooser.ChooseDefault(Of("av1_nvenc", "h264_mf"))!.Encoder.Id);
    }

    [Fact]
    public void Among_gpus_the_catalog_order_decides_the_vendor()
    {
        Assert.Equal("h264_amf", ExportEncoderChooser.ChooseDefault(Of("h264_qsv", "h264_amf"))!.Encoder.Id);
        Assert.Equal("h264_nvenc", ExportEncoderChooser.ChooseDefault(Of("h264_qsv", "h264_amf", "h264_nvenc"))!.Encoder.Id);
    }

    [Fact]
    public void The_order_in_which_the_list_arrives_does_not_matter()
    {
        EncoderSpec[] encoders = Of("libvpx-vp9", "libaom-av1", "libsvtav1");

        Assert.Equal("libsvtav1", ExportEncoderChooser.ChooseDefault(encoders)!.Encoder.Id);
        Assert.Equal("libsvtav1", ExportEncoderChooser.ChooseDefault(encoders.Reverse())!.Encoder.Id);
    }

    [Fact]
    public void Without_a_gpu_the_default_is_media_foundation_h264()
    {
        ExportEncoderChoice choice = ExportEncoderChooser.ChooseDefault(Of("libsvtav1", "libvpx-vp9", "hevc_mf", "h264_mf"))!;

        Assert.Equal("h264_mf", choice.Encoder.Id);
        Assert.Null(choice.Note);
    }

    [Fact]
    public void Without_a_gpu_or_media_foundation_the_default_is_av1_then_vp9_and_it_says_why()
    {
        ExportEncoderChoice av1 = ExportEncoderChooser.ChooseDefault(Of("libvpx-vp9", "libaom-av1", "libsvtav1"))!;
        ExportEncoderChoice vp9 = ExportEncoderChooser.ChooseDefault(Of("libvpx-vp9"))!;

        Assert.Equal("libsvtav1", av1.Encoder.Id);
        Assert.Contains("SVT-AV1", av1.Note, StringComparison.Ordinal);
        Assert.Contains("Media Foundation", av1.Note, StringComparison.Ordinal);
        Assert.Equal("libvpx-vp9", vp9.Encoder.Id);
    }

    [Fact]
    public void Kvazaar_and_openh264_are_never_chosen_automatically()
    {
        EncoderSpec[] onlyPatented = Of("libkvazaar", "libopenh264");

        Assert.Null(ExportEncoderChooser.ChooseDefault(onlyPatented));
        Assert.Null(ExportEncoderChooser.Choose(onlyPatented, ExportCodec.H264));
        Assert.Null(ExportEncoderChooser.Choose(onlyPatented, ExportCodec.Hevc));

        EncoderSpec[] withAv1 = Of("libkvazaar", "libopenh264", "libsvtav1");
        Assert.Equal("libsvtav1", ExportEncoderChooser.ChooseDefault(withAv1)!.Encoder.Id);
        Assert.Equal("libsvtav1", ExportEncoderChooser.Choose(withAv1, ExportCodec.Hevc)!.Encoder.Id);
        Assert.Equal("libsvtav1", ExportEncoderChooser.Choose(withAv1, ExportCodec.H264)!.Encoder.Id);
    }

    [Fact]
    public void Kvazaar_and_openh264_are_never_chosen_even_if_a_double_puts_them_first()
    {
        EncoderSpec fake = ExportEncoderCatalog.Find("libkvazaar")! with { Id = "kvazaar-doble" };

        Assert.Equal("libvpx-vp9", ExportEncoderChooser.Choose([fake, .. Of("libvpx-vp9")], ExportCodec.Hevc)!.Encoder.Id);
    }

    [Fact]
    public void Requested_h264_with_a_gpu_uses_it()
    {
        ExportEncoderChoice choice = ExportEncoderChooser.Choose(Of("libsvtav1", "h264_mf", "h264_qsv"), ExportCodec.H264)!;

        Assert.Equal("h264_qsv", choice.Encoder.Id);
        Assert.Equal(ExportCodec.H264, choice.Requested);
        Assert.False(choice.Substituted);
        Assert.Null(choice.Note);
    }

    [Fact]
    public void Requested_h264_without_a_gpu_uses_media_foundation()
    {
        ExportEncoderChoice choice = ExportEncoderChooser.Choose(Of("libsvtav1", "libopenh264", "h264_mf"), ExportCodec.H264)!;

        Assert.Equal("h264_mf", choice.Encoder.Id);
        Assert.False(choice.Substituted);
    }

    [Fact]
    public void Requested_h264_without_a_gpu_or_media_foundation_falls_back_to_av1_then_vp9_and_says_so()
    {
        ExportEncoderChoice av1 = ExportEncoderChooser.Choose(Of("libopenh264", "libvpx-vp9", "libsvtav1"), ExportCodec.H264)!;
        ExportEncoderChoice vp9 = ExportEncoderChooser.Choose(Of("libopenh264", "libvpx-vp9"), ExportCodec.H264)!;

        Assert.Equal("libsvtav1", av1.Encoder.Id);
        Assert.True(av1.Substituted);
        Assert.Contains("H.264", av1.Note, StringComparison.Ordinal);
        Assert.Contains("SVT-AV1", av1.Note, StringComparison.Ordinal);
        Assert.Equal("libvpx-vp9", vp9.Encoder.Id);
        Assert.True(vp9.Substituted);
    }

    [Fact]
    public void Requested_hevc_without_a_gpu_uses_media_foundation_or_falls_back_like_h264()
    {
        Assert.Equal("hevc_mf", ExportEncoderChooser.Choose(Of("libsvtav1", "hevc_mf"), ExportCodec.Hevc)!.Encoder.Id);

        ExportEncoderChoice fallback = ExportEncoderChooser.Choose(Of("libkvazaar", "libaom-av1"), ExportCodec.Hevc)!;
        Assert.Equal("libaom-av1", fallback.Encoder.Id);
        Assert.Contains("HEVC", fallback.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Patent_free_codecs_are_never_substituted()
    {
        Assert.Null(ExportEncoderChooser.Choose(Of("libvpx-vp9", "h264_nvenc"), ExportCodec.Av1));
        Assert.Null(ExportEncoderChooser.Choose(Of("libsvtav1"), ExportCodec.Vp9));
        Assert.Null(ExportEncoderChooser.Choose(Of("libsvtav1"), ExportCodec.ProRes));
        Assert.Equal("prores_ks", ExportEncoderChooser.Choose(Of("libsvtav1", "prores_ks"), ExportCodec.ProRes)!.Encoder.Id);
    }

    [Fact]
    public void The_video_default_ignores_masters_and_audio()
    {
        Assert.Null(ExportEncoderChooser.ChooseDefault(Of("prores_ks", "ffv1", "png", "aac")));
    }

    [Fact]
    public void Audio_prefers_the_windows_aac()
    {
        Assert.Equal("aac_mf", ExportEncoderChooser.ChooseDefaultAudio(Of("aac", "libopus", "aac_mf"))!.Encoder.Id);
        Assert.Equal("aac", ExportEncoderChooser.ChooseDefaultAudio(Of("libopus", "aac"))!.Encoder.Id);
        Assert.Null(ExportEncoderChooser.ChooseDefaultAudio(Of("libopus")));
        Assert.Equal("aac_mf", ExportEncoderChooser.Choose(Of("aac", "aac_mf"), ExportCodec.Aac)!.Encoder.Id);
        Assert.Equal("libopus", ExportEncoderChooser.Choose(Of("aac", "libopus"), ExportCodec.Opus)!.Encoder.Id);
        Assert.Equal("pcm_s16le", ExportEncoderChooser.Choose(Of("pcm_s24le", "pcm_s16le"), ExportCodec.Pcm)!.Encoder.Id);
    }

    [Fact]
    public void A_manual_choice_allows_kvazaar_and_openh264_with_their_warning()
    {
        EncoderSpec[] available = Of("h264_nvenc", "libkvazaar", "libopenh264");

        ExportEncoderChoice kvazaar = ExportEncoderChooser.ChooseById(available, "libkvazaar")!;
        Assert.Equal("libkvazaar", kvazaar.Encoder.Id);
        Assert.Contains("Patentes", kvazaar.Note, StringComparison.Ordinal);
        Assert.Contains("calidad baja", ExportEncoderChooser.ChooseById(available, "libopenh264")!.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ExportEncoderChooser.ChooseById(available, "h264_nvenc")!.Note);
        Assert.Null(ExportEncoderChooser.ChooseById(available, "libsvtav1"));
    }

    [Fact]
    public void Duplicates_and_unknown_doubles_do_not_break_the_order()
    {
        EncoderSpec stranger = ExportEncoderCatalog.Find("libsvtav1")! with { Id = "av1-doble" };
        EncoderSpec[] available = [stranger, .. Of("libvpx-vp9", "libvpx-vp9", "libsvtav1")];

        Assert.Equal("libsvtav1", ExportEncoderChooser.ChooseDefault(available)!.Encoder.Id);
        Assert.Equal("av1-doble", ExportEncoderChooser.ChooseDefault([stranger, .. Of("libvpx-vp9")])!.Encoder.Id);
    }
}
