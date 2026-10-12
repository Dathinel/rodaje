using Rodaje.App.Views.Export;
using Rodaje.Export;
using Rodaje.Media.Export;

namespace Rodaje.App.Tests.Export;

public class ExportSettingsEditorTests
{
    private static readonly IReadOnlyList<EncoderSpec> All = SimulatedEncoders.All;

    public static TheoryData<string> FactoryPresets() => [.. ExportPresetLibrary.Factory.Select(p => p.Id)];

    [Theory]
    [MemberData(nameof(FactoryPresets))]
    public void LosPreajustesDeFabrica_SeExportanSinCambiosNiProblemas(string id)
    {
        ExportPreset preset = ExportPresetLibrary.Factory.Single(p => p.Id == id);

        CoercedSettings coerced = ExportSettingsEditor.Coerce(preset.Settings, All);

        Assert.Empty(coerced.Changes);
        Assert.Empty(ExportSettingsEditor.Validate(coerced.Settings, All));
        Assert.True(ExportPresetLibrary.Matches(preset, coerced.Settings));
    }

    [Fact]
    public void WebM_CambiaH264PorVp9YAacPorOpus_YDicepor_que()
    {
        ExportSettings settings = ExportPresets.YouTube1080p.Settings with { Container = ExportContainer.WebM };

        CoercedSettings coerced = ExportSettingsEditor.Coerce(settings, All);

        Assert.Equal(ExportCodec.Vp9, coerced.Settings.Video!.Codec);
        Assert.Equal(ExportCodec.Opus, coerced.Settings.Audio!.Codec);
        Assert.Contains(coerced.Changes, c => c.Contains("WebM no admite H.264", StringComparison.Ordinal));
        Assert.Contains(coerced.Changes, c => c.Contains("audio AAC", StringComparison.Ordinal));
        Assert.Empty(ExportSettingsEditor.Validate(coerced.Settings, All));
    }

    [Fact]
    public void Gif_NoLlevaAudio()
    {
        ExportSettings settings = ExportPresets.Gif.Settings with { Audio = new AudioExportSettings { Codec = ExportCodec.Aac } };

        CoercedSettings coerced = ExportSettingsEditor.Coerce(settings, All);

        Assert.Null(coerced.Settings.Audio);
        Assert.Contains(coerced.Changes, c => c.Contains("no lleva sonido", StringComparison.Ordinal));
    }

    [Fact]
    public void DiezBitsEnH264_BajaAOcho()
    {
        ExportSettings settings = ExportPresets.YouTube1080p.Settings;
        settings = settings with { Video = settings.Video! with { BitDepth = 10 } };

        CoercedSettings coerced = ExportSettingsEditor.Coerce(settings, All);

        Assert.Equal(8, coerced.Settings.Video!.BitDepth);
        Assert.Contains(coerced.Changes, c => c.Contains("10 bits", StringComparison.Ordinal));
    }

    [Fact]
    public void DiezBitsEnHevc_PasaElPerfilAMain10()
    {
        ExportSettings settings = ExportPresets.YouTube4K.Settings;
        settings = settings with { Video = settings.Video! with { BitDepth = 10 } };

        CoercedSettings coerced = ExportSettingsEditor.Coerce(settings, All);

        Assert.Equal("main10", coerced.Settings.Video!.Profile);
        Assert.Empty(ExportSettingsEditor.Validate(coerced.Settings, All));
    }

    [Fact]
    public void SinGpuNiMediaFoundation_H264CaeAAv1PorCpu()
    {
        EncoderSpec[] software = [.. All.Where(e => !e.IsHardware && e.Vendor != EncoderVendor.Microsoft)];

        CoercedSettings coerced = ExportSettingsEditor.Coerce(ExportPresets.YouTube1080p.Settings, software);

        Assert.Equal(ExportCodec.Av1, coerced.Settings.Video!.Codec);
        Assert.Equal("libsvtav1", ExportSettingsEditor.Encoders(coerced.Settings, software).Video!.Id);
        Assert.Equal("aac", ExportSettingsEditor.Encoders(coerced.Settings, software).Audio!.Id);
        Assert.Empty(ExportSettingsEditor.Validate(coerced.Settings, software));
    }

    [Fact]
    public void UnCodificadorFueraDeLosValoresPorDefecto_SoloSiSeElige()
    {
        var kvazaar = new EncoderSpec
        {
            Id = "libkvazaar", Codec = ExportCodec.Hevc, IsHardware = false, Vendor = EncoderVendor.Software,
            License = EncoderLicense.Permissive, InDefaults = false, Note = "Patentes de HEVC sin cubrir: no por defecto.",
            PixelFormats = [ExportPixelFormat.Yuv420P], RateControls = [RateControlMode.ConstantQuality],
        };
        EncoderSpec[] encoders = [kvazaar, SimulatedEncoders.SvtAv1];

        Assert.Null(ExportChoices.DefaultEncoder(ExportCodec.Hevc, encoders));
        Assert.Same(kvazaar, ExportChoices.Resolve(ExportCodec.Hevc, "libkvazaar", encoders));
        Assert.Contains("no por defecto", ExportChoices.EncoderLabel(kvazaar), StringComparison.Ordinal);
    }

    [Fact]
    public void TamanoObjetivoSinDosPasadas_PasaACalidadConstante()
    {
        // SVT-AV1 no hace dos pasadas por FFmpeg; NVENC sí (en la misma codificación, con -multipass).
        ExportSettings settings = ExportPresets.SmallFile.Settings;
        settings = settings with { Video = settings.Video! with { RateControl = RateControl.TargetSize(50_000_000) } };

        CoercedSettings coerced = ExportSettingsEditor.Coerce(settings, All);

        Assert.Equal(RateControlMode.ConstantQuality, coerced.Settings.Video!.RateControl.Mode);
        Assert.Single(coerced.Changes);
        Assert.Contains(RateChoice.TargetSize, ExportChoices.RateChoices(SimulatedEncoders.H264Nvenc));
        Assert.DoesNotContain(RateChoice.TargetSize, ExportChoices.RateChoices(SimulatedEncoders.SvtAv1));
    }

    [Fact]
    public void TasaIncoherente_QuedaComoProblema()
    {
        ExportSettings settings = ExportPresets.YouTube1080p.Settings;
        settings = settings with { Video = settings.Video! with { RateControl = RateControl.Vbr(20_000, 10_000) } };

        CoercedSettings coerced = ExportSettingsEditor.Coerce(settings, All);

        Assert.Contains(ExportSettingsEditor.Validate(coerced.Settings, All), p => p.Code == ExportProblemCode.InvalidValue);
    }

    [Fact]
    public void CalidadFueraDeEscala_VuelveALaDelCodificador()
    {
        ExportSettings settings = ExportPresets.SmallFile.Settings with { Container = ExportContainer.Mp4 };
        settings = settings with { Video = settings.Video! with { Codec = ExportCodec.H264, RateControl = RateControl.ConstantQuality(60) } };

        CoercedSettings coerced = ExportSettingsEditor.Coerce(settings, All);

        Assert.Null(coerced.Settings.Video!.RateControl.Quality);
        Assert.Empty(ExportSettingsEditor.Validate(coerced.Settings, All));
    }

    [Fact]
    public void LasListasSoloOfrecenLoPosible()
    {
        Assert.Equal([ExportCodec.Vp9, ExportCodec.Av1], ExportChoices.VideoCodecs(ExportContainer.WebM, All));
        Assert.Equal([ExportCodec.Opus], ExportChoices.AudioCodecs(ExportContainer.WebM, All));
        Assert.Contains(ExportContainer.WebP, ExportChoices.Containers(All));
        Assert.DoesNotContain(ExportContainer.WebP, ExportChoices.Containers([.. All.Where(e => e.Id != "libwebp")]));
        Assert.Contains(ExportContainer.Wav, ExportChoices.Containers(All));
        Assert.Equal([8, 10], ExportChoices.BitDepths(SimulatedEncoders.HevcNvenc));
        Assert.Equal([1, 2, 6], ExportChoices.Channels(SimulatedEncoders.AacMf));
        Assert.Equal([1, 2], ExportChoices.Channels(SimulatedEncoders.Mp3));
        Assert.StartsWith("GPU · NVIDIA", ExportChoices.EncoderLabel(SimulatedEncoders.H264Nvenc), StringComparison.Ordinal);
        Assert.StartsWith("CPU", ExportChoices.EncoderLabel(SimulatedEncoders.SvtAv1), StringComparison.Ordinal);
        Assert.Equal(["h264_nvenc", "h264_mf", "libopenh264"], ExportChoices.EncodersFor(ExportCodec.H264, All).Select(e => e.Id));
    }

    [Fact]
    public void SoloAudio_CreaElAudioSiFaltaYQuitaElVideo()
    {
        ExportSettings settings = ExportPresets.YouTube1080p.Settings with { Container = ExportContainer.Wav, Audio = null };

        CoercedSettings coerced = ExportSettingsEditor.Coerce(settings, All);

        Assert.Null(coerced.Settings.Video);
        Assert.Equal(ExportCodec.Pcm, coerced.Settings.Audio!.Codec);
        Assert.Equal(RateControlMode.Lossless, coerced.Settings.Audio.RateControl.Mode);
        Assert.Empty(ExportSettingsEditor.Validate(coerced.Settings, All));
    }

    [Fact]
    public void AudioConCanalesOTasaQueNoAdmite_SeAcerca()
    {
        ExportSettings settings = ExportPresets.YouTube1080p.Settings with { Container = ExportContainer.Mkv };
        settings = settings with { Audio = new AudioExportSettings { Codec = ExportCodec.Mp3, Channels = 6, SampleRate = 96_000 } };

        CoercedSettings coerced = ExportSettingsEditor.Coerce(settings, All);

        Assert.Equal(2, coerced.Settings.Audio!.Channels);
        Assert.Equal(48_000, coerced.Settings.Audio.SampleRate);
        Assert.Empty(ExportSettingsEditor.Validate(coerced.Settings, All));
    }

    [Fact]
    public void SinH264NiHevc_ComoWindowsNoServer_DiceQueElijaAv1OVp9_YAv1Exporta()
    {
        EncoderSpec[] sinH26x = [.. All.Where(e => e.Codec is not (ExportCodec.H264 or ExportCodec.Hevc))];
        ExportSettings settings = ExportPresets.YouTube1080p.Settings;

        Assert.Equal(ExportSettingsEditor.NoH26xMessage, Assert.Single(ExportSettingsEditor.Validate(settings, sinH26x)).Message);
        Assert.Empty(ExportSettingsEditor.Validate(settings with { Video = ExportSettingsEditor.DefaultVideo(ExportCodec.Av1, sinH26x) }, sinH26x));
    }
}
