using System.Buffers.Binary;
using Rodaje.Core.Timing;
using Rodaje.Media.Export;

namespace Rodaje.Export.Tests.Cpu;

/// <summary>
/// La entrada de <see cref="CpuExportTests"/> se hace sin <c>ffmpeg.exe</c> (tarea 330), así que estas pruebas corren
/// también en Linux, donde no lo hay.
/// </summary>
public sealed class CpuExportInputsTests
{
    [Theory]
    [InlineData(ExportPixelFormat.Nv12)]
    [InlineData(ExportPixelFormat.Yuv420P)]
    [InlineData(ExportPixelFormat.Yuv420P10)]
    [InlineData(ExportPixelFormat.Yuv422P)]
    [InlineData(ExportPixelFormat.Yuv422P10)]
    [InlineData(ExportPixelFormat.Yuva444P10)]
    public void Synthetic_frames_are_made_without_ffmpeg_and_differ_from_one_another(ExportPixelFormat pixels)
    {
        ExportVideoFormat format = new(640, 360, Rational.Fps30, pixels, ExportColor.For(640, 360, pixels));

        LavfiFrames source = LavfiFrames.Synthetic(format, distinct: 15);

        int frameBytes = checked((int)pixels.FrameBytes(640, 360));
        Assert.Equal(15, source.Distinct);
        Assert.Equal(frameBytes, source.Packed(0).Length);
        Assert.False(source.Packed(0).SequenceEqual(source.Packed(1)));
        Assert.True(source.Packed(3).SequenceEqual(source.Packed(18)));
        if (pixels.BytesPerSample() == 2)
        {
            ReadOnlySpan<byte> packed = source.Packed(4);
            for (int i = 0; i < packed.Length; i += 2)
            {
                Assert.InRange(BinaryPrimitives.ReadUInt16LittleEndian(packed[i..]), 0, (1 << pixels.BitDepth()) - 1);
            }
        }

        ExportVideoFrame frame = source.Frame(16);
        Assert.Equal(16, frame.Index);
    }

    [Fact]
    public void The_sine_wav_is_written_without_ffmpeg_with_every_sample()
    {
        using var scratch = new ExportScratch();

        string wav = CpuExportEnvironment.SineWav(scratch.Combine("mezcla.rodaje-tmp.wav"), 45 / 30.0);

        byte[] bytes = File.ReadAllBytes(wav);
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal("WAVEfmt "u8.ToArray(), bytes[8..16]);
        Assert.Equal(3, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(20)));
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22)));
        Assert.Equal(48_000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(32, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(34)));
        Assert.Equal("data"u8.ToArray(), bytes[36..40]);
        int dataBytes = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40));
        Assert.Equal(72_000 * 2 * sizeof(float), dataBytes);
        Assert.Equal(44 + dataBytes, bytes.Length);
        Assert.Equal(bytes.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
        float peak = 0;
        for (int i = 44; i < bytes.Length; i += 4)
        {
            peak = Math.Max(peak, Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i))));
        }

        Assert.InRange(peak, 0.12f, 0.125f);
    }
}
