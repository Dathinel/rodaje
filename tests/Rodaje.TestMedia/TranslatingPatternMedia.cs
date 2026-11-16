namespace Rodaje.TestMedia;

/// <summary>
/// Patrón sintético de textura continua que se traslada un número fijo de píxeles por cuadro en horizontal.
/// Permite evaluar la interpolación de cuadros (flujo óptico, mezcla y cuadro más cercano) contra una verdad
/// matemática exacta en cualquier fracción t.
/// </summary>
public static class TranslatingPatternMedia
{
    public const int DefaultWidth = 320;
    public const int DefaultHeight = 180;
    public const int DefaultShiftPerFrame = 8;
    public const int DefaultFrameCount = 30;

    /// <summary>Evalúa la textura continua de luma en la coordenada (x, y).</summary>
    public static float SampleLuma(float x, float y)
    {
        float v = 0.5f
            + (0.16f * MathF.Sin((x * 0.12f) + 0.3f) * MathF.Cos((y * 0.15f) + 0.5f))
            + (0.12f * MathF.Cos((x * 0.08f) - 0.2f))
            + (0.10f * MathF.Sin(((x + y) * 0.07f) + 1.1f))
            + (0.06f * MathF.Cos(((2f * x - y) * 0.06f) + 0.4f))
            // Detalle de 24 px de periodo: con 8 px por cuadro la mezcla lo aplana y el flujo no.
            + (0.14f * MathF.Sin((x * 0.26f) + 0.9f) * MathF.Cos((y * 0.21f) + 0.2f));
        return Math.Clamp(v, 0.05f, 0.95f);
    }

    /// <summary>Evalúa la textura continua de croma (U, V) en la coordenada (x, y).</summary>
    public static (float U, float V) SampleChroma(float x, float y)
    {
        float u = 0.5f
            + (0.15f * MathF.Sin((x * 0.14f) + 0.4f) * MathF.Cos((y * 0.11f) + 0.8f))
            + (0.10f * MathF.Cos((x * 0.09f) - 0.3f));
        float v = 0.5f
            + (0.15f * MathF.Cos((x * 0.12f) + 0.7f) * MathF.Sin((y * 0.13f) + 0.2f))
            + (0.10f * MathF.Sin(((x - y) * 0.08f) + 0.5f));
        return (Math.Clamp(u, 0.05f, 0.95f), Math.Clamp(v, 0.05f, 0.95f));
    }

    /// <summary>Genera el plano de luma exacto para una fracción t entre cuadros.</summary>
    public static byte[] CreateTruthLuma(float fraction, int width = DefaultWidth, int height = DefaultHeight, int shiftPerFrame = DefaultShiftPerFrame, int baseFrame = 0)
    {
        byte[] luma = new byte[width * height];
        float totalShift = (baseFrame + fraction) * shiftPerFrame;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                float sample = SampleLuma(x - totalShift, y);
                luma[row + x] = (byte)Math.Clamp(MathF.Round(sample * 255f), 0f, 255f);
            }
        }

        return luma;
    }

    /// <summary>Calcula el PSNR de la luma entre la imagen real y la de referencia, excluyendo los márgenes laterales.</summary>
    public static double ComputePsnr(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected, int width = DefaultWidth, int height = DefaultHeight, int margin = 16)
    {
        double sumSq = 0;
        int count = 0;
        for (int y = margin; y < height - margin; y++)
        {
            int row = y * width;
            for (int x = margin; x < width - margin; x++)
            {
                double diff = actual[row + x] - expected[row + x];
                sumSq += diff * diff;
                count++;
            }
        }

        double mse = sumSq / count;
        if (mse <= 1e-12)
        {
            return 99.0;
        }

        return 10.0 * Math.Log10((255.0 * 255.0) / mse);
    }

    /// <summary>Crea un archivo de video H.264 con el patrón de traslación.</summary>
    public static string CreateVideo(string path, int frameCount = DefaultFrameCount, int width = DefaultWidth, int height = DefaultHeight, int shiftPerFrame = DefaultShiftPerFrame)
    {
        if (File.Exists(path))
        {
            return path;
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        FfmpegTool ffmpeg = FfmpegTool.Default ?? throw new InvalidOperationException("FfmpegTool no está disponible.");
        string encoder = ffmpeg.FindVideoEncoder(VideoCodec.H264) ?? "libopenh264";
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "yuv420p",
            "-s", $"{width}x{height}",
            "-framerate", "30",
            "-i", "pipe:0",
            "-c:v", encoder,
        };
        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            args.AddRange(["-preset", "p4", "-rc", "constqp", "-qp", "12", "-g", "30", "-no-scenecut", "1", "-strict_gop", "1"]);
        }
        else if (encoder.EndsWith("_mf", StringComparison.Ordinal))
        {
            args.AddRange(["-rate_control", "cbr", "-b:v", "8000k", "-g", "30"]);
        }
        else
        {
            args.AddRange(["-b:v", "8000k", "-g", "30"]);
        }

        args.AddRange(["-pix_fmt", "yuv420p", path]);

        int ySize = width * height;
        int uvW = width / 2;
        int uvH = height / 2;
        int uvSize = uvW * uvH;
        byte[] frameBuffer = new byte[ySize + (uvSize * 2)];

        ffmpeg.Run(args, input =>
        {
            for (int f = 0; f < frameCount; f++)
            {
                float shift = f * shiftPerFrame;
                for (int y = 0; y < height; y++)
                {
                    int row = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        frameBuffer[row + x] = (byte)Math.Clamp(MathF.Round(SampleLuma(x - shift, y) * 255f), 0f, 255f);
                    }
                }

                int uOffset = ySize;
                int vOffset = ySize + uvSize;
                float chromaShift = shift / 2f;
                for (int y = 0; y < uvH; y++)
                {
                    int row = y * uvW;
                    for (int x = 0; x < uvW; x++)
                    {
                        (float u, float v) = SampleChroma(x - chromaShift, y);
                        frameBuffer[uOffset + row + x] = (byte)Math.Clamp(MathF.Round(u * 255f), 0f, 255f);
                        frameBuffer[vOffset + row + x] = (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
                    }
                }

                input.Write(frameBuffer);
            }
        });

        return path;
    }
}
