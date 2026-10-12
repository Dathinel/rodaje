using System.Text.Json;

namespace Rodaje.Module.Raw;

/// <summary>De dónde sale el balance de blancos.</summary>
internal enum WhiteBalanceMode
{
    /// <summary>El de la toma (<c>AsShotNeutral</c> en un DNG).</summary>
    Camera,

    /// <summary>Luz de día D65 de la matriz de color de la cámara (sin el de la toma).</summary>
    Daylight,

    /// <summary>Multiplicadores R, G, B dados por el editor.</summary>
    Custom,
}

/// <summary>Primarios de la salida (los de LibRaw: <c>output_color</c>).</summary>
internal enum OutputPrimaries
{
    Rec709 = 1,
    ProPhoto = 4,
    Aces = 6,
    P3 = 7,
    Rec2020 = 8,
}

/// <summary>Curva de transferencia de la salida.</summary>
internal enum OutputCurve
{
    Rec709,
    Srgb,
    Linear,
}

/// <summary>
/// Cómo se revela un cuadro: balance, exposición en pasos (EV, en luz lineal antes de la curva), primarios, curva y
/// calidad del interpolado de Bayer. Se lee de los parámetros de la llamada; lo que falta toma el valor por omisión.
/// </summary>
internal sealed record DevelopSettings
{
    public const double MaxExposure = 6;

    public WhiteBalanceMode WhiteBalance { get; init; } = WhiteBalanceMode.Camera;

    /// <summary>R, G, B para <see cref="WhiteBalanceMode.Custom"/>.</summary>
    public IReadOnlyList<float> Multipliers { get; init; } = [];

    public double Exposure { get; init; }

    public OutputPrimaries Primaries { get; init; } = OutputPrimaries.Rec709;

    public OutputCurve Curve { get; init; } = OutputCurve.Rec709;

    /// <summary><c>false</c>: interpolado lineal (rápido); <c>true</c>: AHD (más nítido, sin falsos colores).</summary>
    public bool HighQuality { get; init; } = true;

    /// <summary>Lee los parámetros; un valor fuera de rango o de otro tipo es un error con un texto para la persona.</summary>
    /// <exception cref="ArgumentException">Parámetros no válidos.</exception>
    public static DevelopSettings Parse(JsonElement? parameters)
    {
        var settings = new DevelopSettings();
        if (parameters is not { ValueKind: JsonValueKind.Object } p)
        {
            return settings;
        }

        if (p.TryGetProperty("balance", out JsonElement balance))
        {
            settings = balance.ValueKind switch
            {
                JsonValueKind.String when balance.GetString() == "camara" => settings with { WhiteBalance = WhiteBalanceMode.Camera },
                JsonValueKind.String when balance.GetString() == "luz-dia" => settings with { WhiteBalance = WhiteBalanceMode.Daylight },
                JsonValueKind.Array => settings with { WhiteBalance = WhiteBalanceMode.Custom, Multipliers = Multipliers3(balance) },
                _ => throw new ArgumentException("«balance» es «camara», «luz-dia» o tres multiplicadores [r, g, b]."),
            };
        }

        if (p.TryGetProperty("exposicion", out JsonElement exposure))
        {
            if (exposure.ValueKind != JsonValueKind.Number || !exposure.TryGetDouble(out double ev) || !double.IsFinite(ev) || Math.Abs(ev) > MaxExposure)
            {
                throw new ArgumentException($"«exposicion» va de −{MaxExposure} a +{MaxExposure} pasos.");
            }

            settings = settings with { Exposure = ev };
        }

        if (p.TryGetProperty("espacio", out JsonElement space))
        {
            settings = settings with
            {
                Primaries = Text(space) switch
                {
                    "rec709" or "srgb" => OutputPrimaries.Rec709,
                    "rec2020" => OutputPrimaries.Rec2020,
                    "p3" => OutputPrimaries.P3,
                    "aces" => OutputPrimaries.Aces,
                    "prophoto" => OutputPrimaries.ProPhoto,
                    _ => throw new ArgumentException("«espacio» es rec709, rec2020, p3, aces o prophoto."),
                },
            };
        }

        if (p.TryGetProperty("curva", out JsonElement curve))
        {
            settings = settings with
            {
                Curve = Text(curve) switch
                {
                    "rec709" => OutputCurve.Rec709,
                    "srgb" => OutputCurve.Srgb,
                    "lineal" => OutputCurve.Linear,
                    _ => throw new ArgumentException("«curva» es rec709, srgb o lineal."),
                },
            };
        }

        if (p.TryGetProperty("calidad", out JsonElement quality))
        {
            settings = settings with
            {
                HighQuality = Text(quality) switch
                {
                    "alta" => true,
                    "rapida" => false,
                    _ => throw new ArgumentException("«calidad» es alta o rapida."),
                },
            };
        }

        return settings;
    }

    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static float[] Multipliers3(JsonElement array)
    {
        if (array.GetArrayLength() != 3)
        {
            throw new ArgumentException("«balance» necesita tres multiplicadores [r, g, b].");
        }

        var values = new float[3];
        int i = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double v) || !double.IsFinite(v) || v is < 0.05 or > 20)
            {
                throw new ArgumentException("Cada multiplicador de «balance» va de 0,05 a 20.");
            }

            values[i++] = (float)v;
        }

        return values;
    }
}
