using FotoToma.Media.Gpu;
using FotoToma.Media.Video;

namespace FotoToma.Media.Tests.Support;

public enum DecodeMode
{
    /// <summary>D3D11VA sobre el dispositivo compartido: los cuadros son capas del pool del decodificador.</summary>
    Hardware,

    /// <summary>Decodificación por software y subida a una textura NV12/P010 propia.</summary>
    SoftwareUpload,

    /// <summary>Sin dispositivo: los cuadros quedan en la CPU.</summary>
    SoftwareCpu,
}

internal static class DecodeModes
{
    public static VideoDecoderOptions Options(DecodeMode mode, GpuDevice? device = null) => mode switch
    {
        DecodeMode.Hardware => new VideoDecoderOptions { Device = device ?? MediaTestEnvironment.Gpu },
        DecodeMode.SoftwareUpload => new VideoDecoderOptions { Device = device ?? MediaTestEnvironment.Gpu, AllowHardwareDecoding = false },
        _ => new VideoDecoderOptions(),
    };

    /// <summary>Combina clips y modos; los de GPU solo si hay GPU (las pruebas de hardware van aparte, con su motivo).</summary>
    public static TheoryData<string, DecodeMode> Software(params ClipSpec[] clips)
    {
        var data = new TheoryData<string, DecodeMode>();
        foreach (ClipSpec clip in clips)
        {
            data.Add(clip.Name, DecodeMode.SoftwareCpu);
            if (MediaTestEnvironment.Gpu is not null)
            {
                data.Add(clip.Name, DecodeMode.SoftwareUpload);
            }
        }

        return data;
    }

    public static ClipSpec Clip(string name) =>
        new[] { TestClips.H264, TestClips.Hevc, TestClips.Hevc10, TestClips.H264Mkv }.Single(c => c.Name == name);
}

/// <summary>Teoría que necesita FFmpeg y D3D11VA.</summary>
internal sealed class HardwareTheoryAttribute : TheoryAttribute
{
    public HardwareTheoryAttribute() => Skip = MediaTestEnvironment.HardwareSkipReason;
}
