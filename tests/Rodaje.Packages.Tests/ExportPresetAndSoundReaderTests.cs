using System.Buffers.Binary;
using System.Text;
using Rodaje.Packages.Content;

namespace Rodaje.Packages.Tests;

/// <summary><c>rodaje.exportpreset</c> v1 y los WAV de los elementos <c>sound</c>: lectura, valores por defecto y rechazos.</summary>
public sealed class ExportPresetAndSoundReaderTests
{
    private const string Broadcast = """
        { "format": "rodaje.exportpreset", "version": 1, "id": "hd-1080i25", "name": "HD 1080i25 EBU R128",
          "description": "ProRes 422 HQ entrelazado", "category": "broadcast", "container": "mov", "fastStart": false,
          "video": { "codec": "prores", "profile": "hq", "bitDepth": 10, "chroma": "422", "width": 1920, "height": 1080,
                     "frameRate": 25, "fieldOrder": "tff", "rateControl": "profile" },
          "audio": { "codec": "pcm", "sampleFormat": "s24", "rateControl": "lossless", "loudness": { "lufs": -23, "truePeak": -1 } },
          "otro": "se ignora" }
        """;

    private static MemoryStream Utf8(string json) => new(Encoding.UTF8.GetBytes(json));

    private static string Error(string json) =>
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadExportPreset(Utf8(json))).Message;

    private static string With(string video, string extra = "") =>
        $$"""{ "format": "rodaje.exportpreset", "version": 1, "name": "P", "container": "mp4"{{extra}}, "video": { {{video}} } }""";

    [Fact]
    public void Lee_un_preajuste_de_entrega_entrelazado()
    {
        ExportPresetDefinition preset = ContentReaders.ReadExportPreset(Utf8(Broadcast));

        Assert.Equal(("hd-1080i25", "HD 1080i25 EBU R128", "broadcast", "mov", false), (preset.Id, preset.Name, preset.Category, preset.Container, preset.FastStart));
        ExportPresetVideo video = preset.Video!;
        Assert.Equal(("prores", "hq", 10, "422", 1920, 1080), (video.Codec, video.Profile, video.BitDepth, video.Chroma, video.Width!.Value, video.Height!.Value));
        Assert.Equal((25, 1, "tff", "profile"), (video.FrameRateNumerator, video.FrameRateDenominator, video.FieldOrder, video.RateMode));
        ExportPresetAudio audio = preset.Audio!;
        Assert.Equal(("pcm", "s24", "lossless", -23.0, -1.0), (audio.Codec, audio.SampleFormat, audio.RateMode, audio.LoudnessLufs!.Value, audio.LoudnessTruePeak));
        Assert.Equal((48_000, 2), (audio.SampleRate, audio.Channels));
    }

    [Theory]
    [InlineData("29.97", 30000, 1001)]
    [InlineData("23.976", 24000, 1001)]
    [InlineData("59.94", 60000, 1001)]
    [InlineData("24", 24, 1)]
    [InlineData("\"30000/1001\"", 30000, 1001)]
    public void Las_tasas_ntsc_se_leen_como_fraccion(string rate, int num, int den)
    {
        ExportPresetVideo video = ContentReaders.ReadExportPreset(Utf8(With($"\"codec\": \"h264\", \"frameRate\": {rate}"))).Video!;
        Assert.Equal((num, den), (video.FrameRateNumerator, video.FrameRateDenominator));
    }

    [Fact]
    public void Sin_tasa_sigue_la_secuencia_y_los_valores_por_defecto()
    {
        ExportPresetDefinition preset = ContentReaders.ReadExportPreset(Utf8(With("\"codec\": \"h264\"")));
        ExportPresetVideo video = preset.Video!;
        Assert.Equal((0, 0, "progressive", "crf", 1, "flat", 8, "420"), (video.FrameRateNumerator, video.FrameRateDenominator, video.FieldOrder,
            video.RateMode, video.Passes, video.Projection, video.BitDepth, video.Chroma));
        Assert.Equal(("master", true, false), (preset.Category, preset.FastStart, preset.Chapters));
        Assert.Null(preset.Audio);
    }

    [Theory]
    [InlineData("\"codec\": \"H264\"", "video.codec")]
    [InlineData("\"codec\": \"h264;rm\"", "video.codec")]
    [InlineData("\"codec\": \"h264\", \"frameRate\": 0", "frameRate")]
    [InlineData("\"codec\": \"h264\", \"frameRate\": 25.5", "frameRate")]
    [InlineData("\"codec\": \"h264\", \"frameRate\": \"25/0\"", "frameRate")]
    [InlineData("\"codec\": \"h264\", \"fieldOrder\": \"tff\"", "entrelazado")]
    [InlineData("\"codec\": \"h264\", \"rateControl\": \"vbr\"", "bitrateKbps")]
    [InlineData("\"codec\": \"h264\", \"passes\": 2", "passes 2")]
    [InlineData("\"codec\": \"h264\", \"bitDepth\": 9", "bitDepth")]
    [InlineData("\"codec\": \"h264\", \"width\": 8", "video.width")]
    [InlineData("\"codec\": \"h264\", \"chroma\": \"411\"", "video.chroma")]
    [InlineData("\"codec\": \"h264\", \"projection\": \"cubemap\"", "video.projection")]
    [InlineData("\"codec\": \"h264\", \"level\": \"4.1; x\"", "video.level")]
    [InlineData("\"codec\": \"4444\"", "video.codec")]
    [InlineData("\"codec\": \"prores\", \"profile\": \"HQ\"", "video.profile")]
    public void Rechaza_lo_que_no_cumple_con_el_campo_en_el_mensaje(string video, string expected) =>
        Assert.Contains(expected, Error(With(video)), StringComparison.Ordinal);

    [Fact]
    public void Rechaza_sin_pistas_categoria_desconocida_y_sonoridad_fuera_de_rango()
    {
        Assert.Contains("video, audio", Error("""{ "format": "rodaje.exportpreset", "version": 1, "name": "P", "container": "mp4" }"""), StringComparison.Ordinal);
        Assert.Contains("category", Error(With("\"codec\": \"h264\"", ", \"category\": \"tv\"")), StringComparison.Ordinal);
        Assert.Contains("lufs", Error("""{ "format": "rodaje.exportpreset", "version": 1, "name": "P", "container": "m4a", "audio": { "codec": "aac", "loudness": { "lufs": 3 } } }"""), StringComparison.Ordinal);
        Assert.Contains("actualiza el programa", Error("""{ "format": "rodaje.exportpreset", "version": 2, "name": "P", "container": "mp4" }"""), StringComparison.Ordinal);
        Assert.Contains("Falta container", Error("""{ "format": "rodaje.exportpreset", "version": 1, "name": "P", "video": { "codec": "h264" } }"""), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 16, 1)]
    [InlineData(2, 16, 1)]
    [InlineData(2, 24, 1)]
    [InlineData(1, 32, 3)]
    public void Lee_wav_pcm_y_float(int channels, int bits, int tag)
    {
        byte[] wav = Wave(48_000, channels, bits, tag, frames: 480, extraChunk: true);

        SoundInfo info = ContentReaders.ReadSoundInfo(new MemoryStream(wav));
        (SoundInfo again, float[] samples) = ContentReaders.ReadSound(new MemoryStream(wav));

        Assert.Equal(new SoundInfo(48_000, channels, bits, tag == 3, 480), info);
        Assert.Equal(info, again);
        Assert.Equal(480 * channels, samples.Length);
        Assert.Equal(0.01, info.DurationSeconds, 9);
        // La primera muestra de cada canal es medio fondo de escala positivo.
        Assert.InRange(samples[0], 0.49, 0.51);
        Assert.All(samples, s => Assert.InRange(s, -1f, 1f));
    }

    [Fact]
    public void Rechaza_wav_que_no_se_puede_usar()
    {
        Assert.Contains("RIFF", SoundError([.. "RIFX"u8, 0, 0, 0, 0, .. "WAVE"u8]), StringComparison.Ordinal);
        Assert.Contains("16, 24 o 32", SoundError(Wave(48_000, 1, 8, 1, 10)), StringComparison.Ordinal);
        Assert.Contains("8 a 192 kHz", SoundError(Wave(4_000, 1, 16, 1, 10)), StringComparison.Ordinal);
        Assert.Contains("1 a 8 canales", SoundError(Wave(48_000, 9, 16, 1, 10)), StringComparison.Ordinal);
        byte[] cut = Wave(48_000, 2, 16, 1, 100);
        Assert.Contains("cortado", Assert.Throws<PackageContentException>(() => ContentReaders.ReadSound(new MemoryStream(cut[..^10]))).Message, StringComparison.Ordinal);
        byte[] noData = Wave(48_000, 1, 16, 1, 10)[..36];
        Assert.Contains("datos", SoundError(noData), StringComparison.Ordinal);
    }

    [Fact]
    public void Mil_wav_danados_solo_lanzan_package_content_exception()
    {
        var random = new Random(36);
        byte[] good = Wave(44_100, 2, 24, 1, 64, extraChunk: true);
        for (int i = 0; i < 1000; i++)
        {
            byte[] bad = (byte[])good.Clone();
            for (int k = random.Next(1, 6); k > 0; k--)
            {
                bad[random.Next(bad.Length)] = (byte)random.Next(256);
            }

            byte[] input = random.Next(4) == 0 ? bad[..random.Next(bad.Length)] : bad;
            try
            {
                ContentReaders.ReadSound(new MemoryStream(input));
            }
            catch (PackageContentException)
            {
            }
        }
    }

    private static string SoundError(byte[] wav) =>
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadSoundInfo(new MemoryStream(wav))).Message;

    /// <summary>Un WAV con la primera muestra de cada canal a medio fondo de escala y el resto en silencio.</summary>
    internal static byte[] Wave(int rate, int channels, int bits, int tag, int frames, bool extraChunk = false)
    {
        int bytes = bits / 8;
        int data = frames * channels * bytes;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8);
        w.Write(0);
        w.Write("WAVE"u8);
        if (extraChunk)
        {
            w.Write("LIST"u8);
            w.Write(3);
            w.Write((byte[])[1, 2, 3, 0]);
        }

        w.Write("fmt "u8);
        w.Write(16);
        w.Write((ushort)tag);
        w.Write((ushort)channels);
        w.Write(rate);
        w.Write(rate * channels * bytes);
        w.Write((ushort)(channels * bytes));
        w.Write((ushort)bits);
        w.Write("data"u8);
        w.Write(data);
        byte[] samples = new byte[data];
        for (int c = 0; c < channels; c++)
        {
            Span<byte> first = samples.AsSpan(c * bytes, bytes);
            switch (bits)
            {
                case 8:
                    first[0] = 192;
                    break;
                case 16:
                    BinaryPrimitives.WriteInt16LittleEndian(first, 16384);
                    break;
                case 24:
                    first[2] = 0x40;
                    break;
                default:
                    if (tag == 3)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(first, 0.5f);
                    }
                    else
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(first, 1 << 30);
                    }

                    break;
            }
        }

        w.Write(samples);
        w.Flush();
        byte[] result = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), result.Length - 8);
        return result;
    }
}
