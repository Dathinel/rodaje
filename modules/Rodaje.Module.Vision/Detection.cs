namespace Rodaje.Module.Vision;

/// <summary>Una caja normalizada a [0,1] del cuadro: esquina superior izquierda, ancho, alto y confianza.</summary>
internal readonly record struct Box(float X, float Y, float W, float H, float P)
{
    public float Area => W * H;

    public static float Iou(Box a, Box b)
    {
        float ix = Math.Max(0, Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X));
        float iy = Math.Max(0, Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y));
        float inter = ix * iy;
        float union = a.Area + b.Area - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>Supresión de no máximos: de mayor a menor confianza, se descarta lo que se solapa más de <paramref name="threshold"/>.</summary>
    public static List<Box> Nms(IEnumerable<Box> boxes, float threshold)
    {
        var kept = new List<Box>();
        foreach (Box box in boxes.OrderByDescending(b => b.P))
        {
            if (kept.TrueForAll(k => Iou(k, box) <= threshold))
            {
                kept.Add(box);
            }
        }

        return kept;
    }

    public Box Clamped()
    {
        float x0 = Math.Clamp(X, 0, 1), y0 = Math.Clamp(Y, 0, 1);
        return new Box(x0, y0, Math.Clamp(X + W, 0, 1) - x0, Math.Clamp(Y + H, 0, 1) - y0, P);
    }
}

/// <summary>
/// Rostros con YuNet (2023mar): entrada fija de 640×640 BGR en 0..255, el cuadro escalado sin deformar arriba a la izquierda
/// y el resto en negro. Decodificación de tres escalas (8, 16, 32) como <c>FaceDetectorYN</c> de OpenCV.
/// </summary>
internal sealed class FaceDetector(VisionModels models)
{
    private const int Size = 640;
    private const float Threshold = 0.6f;
    private static readonly PlanarFormat Format = new(false, [255f, 255f, 255f], [0f, 0f, 0f]);
    private static readonly int[] Strides = [8, 16, 32];
    private static readonly string[] Outputs = ["cls_8", "cls_16", "cls_32", "obj_8", "obj_16", "obj_32", "bbox_8", "bbox_16", "bbox_32"];

    public (List<Box> Boxes, string Device) Detect(BgraFrame frame, ComputeDevice device, CancellationToken cancellationToken = default)
    {
        float scale = (float)Size / Math.Max(frame.Width, frame.Height);
        int cw = Math.Max(1, (int)Math.Round(frame.Width * scale)), ch = Math.Max(1, (int)Math.Round(frame.Height * scale));
        var tensor = new float[3 * Size * Size];
        Imaging.ToPlanar(frame, tensor, 0, Size, Size, cw, ch, Format);
        ModelOutput outputs = models.Run(ModelFiles.YuNet, device, "input", tensor, [1, 3, Size, Size], Outputs, cancellationToken);
        var boxes = new List<Box>();
        float sx = scale * frame.Width, sy = scale * frame.Height;
        for (int s = 0; s < Strides.Length; s++)
        {
            int stride = Strides[s], cols = Size / stride;
            float[] cls = outputs.Values[s], obj = outputs.Values[s + 3], bbox = outputs.Values[s + 6];
            for (int i = 0; i < cls.Length; i++)
            {
                float score = MathF.Sqrt(Math.Clamp(cls[i], 0, 1) * Math.Clamp(obj[i], 0, 1));
                if (score < Threshold)
                {
                    continue;
                }

                float cx = ((i % cols) + bbox[i * 4]) * stride, cy = ((i / cols) + bbox[(i * 4) + 1]) * stride;
                float w = MathF.Exp(bbox[(i * 4) + 2]) * stride, h = MathF.Exp(bbox[(i * 4) + 3]) * stride;
                boxes.Add(new Box((cx - (w / 2)) / sx, (cy - (h / 2)) / sy, w / sx, h / sy, score).Clamped());
            }
        }

        return (Box.Nms(boxes, 0.3f), outputs.Device);
    }
}

/// <summary>
/// Personas con RT-DETRv2-R18 (COCO): entrada RGB en 0..1, estirada; 300 consultas con logits de 80 clases (sigmoide, la
/// clase 0 es «persona») y cajas (cx, cy, w, h) ya normalizadas al cuadro. A 512 en vez de 640 (se entrenó a 640): un 40 %
/// más rápido y sobra para el sujeto de un reencuadre. Lotes de <see cref="Batch"/> cuadros, rellenados si faltan, para
/// que DirectML compile una sola forma (con forma libre va 3,5× más lento).
/// </summary>
internal sealed class PersonDetector(VisionModels models)
{
    public const int Batch = 4;
    private const int Size = 512;
    private const int Classes = 80;
    private const float Threshold = 0.5f;
    private static readonly PlanarFormat Format = new(true, [1f, 1f, 1f], [0f, 0f, 0f]);

    public (List<Box>[] Boxes, string Device) Detect(IReadOnlyList<BgraFrame> frames, ComputeDevice device, CancellationToken cancellationToken = default)
    {
        if (frames.Count > Batch)
        {
            throw new ArgumentOutOfRangeException(nameof(frames));
        }

        int plane = 3 * Size * Size;
        var tensor = new float[Batch * plane];
        for (int f = 0; f < frames.Count; f++)
        {
            Imaging.ToPlanar(frames[f], tensor, f * plane, Size, Size, Size, Size, Format);
        }

        ModelOutput outputs = models.Run(ModelFiles.RtDetr, device, "pixel_values", tensor, [Batch, 3, Size, Size], ["logits", "pred_boxes"], cancellationToken);
        float[] logits = outputs.Values[0], boxes = outputs.Values[1];
        int queries = logits.Length / Batch / Classes;
        var result = new List<Box>[frames.Count];
        for (int f = 0; f < frames.Count; f++)
        {
            var found = new List<Box>();
            for (int q = 0; q < queries; q++)
            {
                int i = (f * queries) + q;
                float p = 1f / (1f + MathF.Exp(-logits[i * Classes]));
                if (p < Threshold)
                {
                    continue;
                }

                float cx = boxes[i * 4], cy = boxes[(i * 4) + 1], w = boxes[(i * 4) + 2], h = boxes[(i * 4) + 3];
                found.Add(new Box(cx - (w / 2), cy - (h / 2), w, h, p).Clamped());
            }

            result[f] = Box.Nms(found, 0.6f);
        }

        return (result, outputs.Device);
    }
}
