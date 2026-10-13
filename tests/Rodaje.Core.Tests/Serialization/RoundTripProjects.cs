using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>
/// Proyectos aleatorios y válidos para las pruebas de ida y vuelta del formato: pistas, clips, efectos conocidos y
/// desconocidos con parámetros y keyframes, títulos, marcadores y regiones, con números y textos en los extremos de lo
/// que el archivo admite.
/// </summary>
/// <remarks>Solo usa <see cref="Random"/> con semilla: el mismo número da siempre el mismo proyecto, en cualquier cultura.</remarks>
internal static class RoundTripProjects
{
    private static readonly Rational[] Rates =
        [Rational.Ntsc24, Rational.Fps24, Rational.Fps25, Rational.Ntsc30, Rational.Fps30, Rational.Fps60, new(1, 1), new(240_000, 1001)];

    private static readonly Interpolation[] Interpolations = Enum.GetValues<Interpolation>();

    /// <summary>Números finitos difíciles de escribir y releer: ±0, subnormales, los extremos y los que no tienen forma corta.</summary>
    private static readonly double[] HardDoubles =
    [
        0, -0.0, 1, -1, 0.1, 0.1 + 0.2, 1.0 / 3, -2.0 / 3, 123456.789, 1e-7, 1e21, 1e-300, 1e300, 5e-324, -5e-324,
        double.Epsilon, double.MaxValue, -double.MaxValue, double.MinValue, 2.2250738585072014E-308, 9007199254740993,
        long.MaxValue, long.MinValue, 4.35, 1.7976931348623155E+308,
    ];

    /// <summary>Textos que cambian con la cultura o con el escape de JSON; el primer grupo no tiene caracteres de control.</summary>
    private static readonly string[] SafeTexts =
    [
        "", "Toma 1", "Vacaciones en Cartagena — edición ñ", "İstanbul ıiIİ", "STRASSE straße Ä", "مرحبا بالعالم ١٢٣",
        "١٫٥ ٪", "1,5 · 1.5 · 1 500", "<script>&amp;</script>", "comillas \" y barra \\ y /", "\t sangría",
        "línea 1\nlínea 2", char.ConvertFromUtf32(0x1F3AC) + " claqueta " + char.ConvertFromUtf32(0x1F600),
        "e" + (char)0x301 + " (combinada)", (char)0x2028 + "separador" + (char)0x2029, (char)0xFEFF + "BOM",
        "no-carácter " + (char)0xFFFF + (char)0xFFFE + (char)0xFDD0, "𝄞 clave de sol",
    ];

    /// <summary>Textos que solo van donde el validador admite caracteres de control (nombres, notas, opciones desconocidas).</summary>
    private static readonly string[] ControlTexts =
    [
        "nulo\0en medio", "\0", "\0\0fin", "campana " + (char)7, (char)0x1B + "[31m escape", "retorno\r\nwindows", (char)0x85 + "NEL", (char)0x7F + "DEL",
    ];

    public static Project Generate(int seed)
    {
        var rng = new Random(seed);
        int nextId = 0;
        string Id(string prefix) => $"{prefix}-{++nextId}";

        var project = new Project { Id = Id("project"), Name = AnyText(rng) };

        int mediaCount = rng.Next(1, 4);
        for (int i = 0; i < mediaCount; i++)
        {
            bool still = rng.Next(4) == 0;
            project.Media.Add(new MediaRef
            {
                Id = Id("media"),
                Path = rng.Next(2) == 0 ? $"material/toma {i} ñ.mp4" : $@"C:\Vídeos\{SafeTexts[rng.Next(2, 6)]}\{i}.mov",
                Name = AnyText(rng),
                // Hasta unos 150 días: tiempos de varios billones de ticks.
                Duration = Time.FromTicks(rng.NextInt64(Time.TicksPerSecond, Time.TicksPerSecond * 13_000_000)),
                HasVideo = true,
                HasAudio = !still,
                FrameRate = still ? null : Pick(rng, Rates),
                Width = rng.Next(1, 8193),
                Height = rng.Next(1, 8193),
                SampleRate = still ? 0 : Pick(rng, [44_100, 48_000, 96_000, 8_000, 192_000]),
                AudioChannels = still ? 0 : rng.Next(1, 9),
            });
        }

        int sequences = rng.Next(1, 3);
        for (int s = 0; s < sequences; s++)
        {
            var sequence = new Sequence
            {
                Id = Id("sequence"),
                Name = AnyText(rng),
                FrameRate = Pick(rng, Rates),
                Width = rng.Next(16, 8193),
                Height = rng.Next(16, 8193),
                SampleRate = Pick(rng, [44_100, 48_000, 96_000]),
                MasterGainDb = rng.Next(3) == 0 ? 0 : Sequence.MinMasterGainDb + (rng.NextDouble() * (Sequence.MaxMasterGainDb - Sequence.MinMasterGainDb)),
            };

            int videoTracks = rng.Next(1, 4);
            for (int t = 0; t < videoTracks; t++)
            {
                sequence.VideoTracks.Add(MakeTrack(rng, project, TrackKind.Video, Id));
            }

            int audioTracks = rng.Next(0, 3);
            for (int t = 0; t < audioTracks; t++)
            {
                sequence.AudioTracks.Add(MakeTrack(rng, project, TrackKind.Audio, Id));
            }

            int markers = rng.Next(0, 5);
            for (int m = 0; m < markers; m++)
            {
                sequence.Markers.Add(new Marker
                {
                    Id = Id("marker"),
                    Time = AnyNonNegativeTime(rng),
                    Name = AnyText(rng),
                    Color = rng.Next(2) == 0 ? null : $"#{rng.Next(0x1000000):X6}",
                    Note = rng.Next(2) == 0 ? null : AnyText(rng),
                });
            }

            int regions = rng.Next(0, 3);
            for (int r = 0; r < regions; r++)
            {
                Time start = Time.FromTicks(rng.NextInt64(0, long.MaxValue / 2));
                sequence.Regions.Add(new Region
                {
                    Id = Id("region"),
                    Start = start,
                    End = start + Time.FromTicks(rng.NextInt64(1, long.MaxValue / 2)),
                    Name = AnyText(rng),
                    Note = rng.Next(2) == 0 ? null : AnyText(rng),
                });
            }

            project.Sequences.Add(sequence);
        }

        return project;
    }

    /// <summary>
    /// Mete en un sitio al azar del proyecto un valor que el archivo no puede guardar sin perderlo: un número NaN o
    /// infinito, o un texto con un sustituto UTF-16 suelto. Devuelve la ruta JSON del sitio.
    /// </summary>
    public static string Break(Project project, Random rng)
    {
        var targets = new List<(string Path, Action<object> Set)>();
        targets.Add(("$.name", v => project.Name = (string)v));
        for (int s = 0; s < project.Sequences.Count; s++)
        {
            Sequence sequence = project.Sequences[s];
            string sp = $"$.sequences[{s}]";
            targets.Add(($"{sp}.masterGainDb", v => sequence.MasterGainDb = (double)v));
            for (int m = 0; m < sequence.Markers.Count; m++)
            {
                Marker marker = sequence.Markers[m];
                targets.Add(($"{sp}.markers[{m}].name", v => marker.Name = (string)v));
            }

            foreach ((string list, List<Track> tracks) in new[] { ("videoTracks", sequence.VideoTracks), ("audioTracks", sequence.AudioTracks) })
            {
                for (int t = 0; t < tracks.Count; t++)
                {
                    Track track = tracks[t];
                    string tp = $"{sp}.{list}[{t}]";
                    targets.Add(($"{tp}.name", v => track.Name = (string)v));
                    AddEffects(targets, track.Effects, $"{tp}.effects");
                    for (int c = 0; c < track.Clips.Count; c++)
                    {
                        AddEffects(targets, track.Clips[c].Effects, $"{tp}.clips[{c}].effects");
                    }
                }
            }
        }

        (string path, Action<object> set) = Pick(rng, targets);
        bool text = path.EndsWith(".name", StringComparison.Ordinal);
        set(text
            ? "roto " + (char)rng.Next(0xD800, 0xE000) + (rng.Next(2) == 0 ? "" : "fin")
            : Pick(rng, [double.NaN, double.PositiveInfinity, double.NegativeInfinity]));
        return path;
    }

    private static void AddEffects(List<(string Path, Action<object> Set)> targets, List<EffectInstance> effects, string prefix)
    {
        for (int e = 0; e < effects.Count; e++)
        {
            foreach ((string name, Parameter parameter) in effects[e].Parameters)
            {
                string pp = $"{prefix}[{e}].parameters";
                if (name.All(char.IsAsciiLetterOrDigit))
                {
                    targets.Add(($"{pp}.{name}.value", v => parameter.Value = (double)v));
                }

                for (int k = 0; k < parameter.Keyframes.Count; k++)
                {
                    int index = k;
                    if (name.All(char.IsAsciiLetterOrDigit))
                    {
                        targets.Add(($"{pp}.{name}.keyframes[{k}].value", v => parameter.Keyframes[index] = parameter.Keyframes[index] with { Value = (double)v }));
                    }
                }
            }
        }
    }

    private static Track MakeTrack(Random rng, Project project, TrackKind kind, Func<string, string> id)
    {
        var track = new Track
        {
            Id = id("track"),
            Kind = kind,
            Name = AnyText(rng),
            Muted = rng.Next(2) == 0,
            Locked = rng.Next(4) == 0,
            Hidden = rng.Next(4) == 0,
            Height = rng.Next(2) == 0 ? null : rng.Next(Track.MinHeight, Track.MaxHeight + 1),
        };

        List<MediaRef> usable = project.Media.FindAll(m => kind == TrackKind.Video ? m.HasVideo : m.HasAudio);
        long at = rng.NextInt64(0, Time.TicksPerSecond * 100);
        int clips = rng.Next(0, 5);
        for (int c = 0; c < clips; c++)
        {
            bool title = kind == TrackKind.Video && (usable.Count == 0 || rng.Next(3) == 0);
            if (!title && usable.Count == 0)
            {
                break;
            }

            Clip clip;
            if (title)
            {
                long length = rng.NextInt64(1, Time.TicksPerSecond * 60);
                clip = new Clip
                {
                    Id = id("title"),
                    SourceIn = Time.Zero,
                    SourceOut = Time.FromTicks(length),
                    Start = Time.FromTicks(at),
                    Generator = new ClipGenerator
                    {
                        Text = Pick(rng, SafeTexts),
                        Style = new TextStyle(),
                        Edges = new GeneratorEdges { In = rng.Next(2) == 0, Out = rng.Next(2) == 0, Before = Time.FromSeconds(rng.Next(0, 5)) },
                    },
                };
            }
            else
            {
                MediaRef media = Pick(rng, usable);
                long sourceIn = rng.NextInt64(0, media.Duration.Ticks - 1);
                long sourceOut = rng.NextInt64(sourceIn + 1, media.Duration.Ticks + 1);
                clip = new Clip
                {
                    Id = id("clip"),
                    MediaId = media.Id,
                    SourceIn = Time.FromTicks(sourceIn),
                    SourceOut = Time.FromTicks(sourceOut),
                    Start = Time.FromTicks(at),
                    Enabled = rng.Next(5) != 0,
                    Color = rng.Next(3) == 0 ? $"#{rng.Next(0x1000000):X6}" : null,
                };
            }

            int effects = rng.Next(0, 3);
            for (int e = 0; e < effects; e++)
            {
                clip.Effects.Add(MakeEffect(rng, kind, id));
            }

            track.Clips.Add(clip);
            at = clip.End.Ticks + (rng.Next(2) == 0 ? 0 : rng.NextInt64(1, Time.TicksPerSecond * 10));
        }

        if (rng.Next(3) == 0)
        {
            track.Effects.Add(MakeEffect(rng, kind, id));
        }

        return track;
    }

    private static EffectInstance MakeEffect(Random rng, TrackKind kind, Func<string, string> id)
    {
        EffectTarget target = kind == TrackKind.Video ? EffectTarget.Video : EffectTarget.Audio;
        EffectDefinition[] known = [.. EffectRegistry.All.Where(d => !d.IsTransition && !d.Hidden && (d.Target & target) != 0 && d.Parameters.Length > 0)];
        var effect = new EffectInstance { Id = id("effect"), Enabled = rng.Next(4) != 0 };

        if (rng.Next(2) == 0 && known.Length > 0)
        {
            // Un efecto conocido: valores dentro de su rango, para que el validador lo acepte.
            EffectDefinition definition = Pick(rng, known);
            effect.Type = definition.Type;
            foreach (ParameterDefinition p in definition.Parameters.Where(_ => rng.Next(2) == 0))
            {
                effect.Parameters[p.Name] = MakeParameter(rng, () => InRange(rng, p));
            }
        }
        else
        {
            // Un efecto de un complemento que esta versión no conoce: el formato lo guarda tal cual, con cualquier número finito.
            effect.Type = "complemento.prueba-" + rng.Next(100);
            int parameters = rng.Next(0, 4);
            for (int i = 0; i < parameters; i++)
            {
                effect.Parameters[$"p{i}-{Pick(rng, SafeTexts)}"] = MakeParameter(rng, () => AnyDouble(rng));
            }

            int options = rng.Next(0, 3);
            for (int i = 0; i < options; i++)
            {
                effect.Options[$"o{i}-{AnyText(rng)}"] = AnyText(rng);
            }
        }

        return effect;
    }

    private static Parameter MakeParameter(Random rng, Func<double> value)
    {
        var parameter = new Parameter { Value = value() };
        int keys = rng.Next(0, 5);
        long at = rng.NextInt64(-Time.TicksPerSecond * 10, Time.TicksPerSecond * 10);
        for (int k = 0; k < keys; k++)
        {
            Interpolation interpolation = Pick(rng, Interpolations);
            var key = new Keyframe(Time.FromTicks(at), value(), interpolation);
            if (interpolation == Interpolation.Bezier && rng.Next(2) == 0)
            {
                key = key with
                {
                    Ease = new BezierEase(rng.NextDouble(), BezierEase.MinY + (rng.NextDouble() * 5), Pick(rng, [0, 1, rng.NextDouble()]), Pick(rng, [BezierEase.MinY, BezierEase.MaxY, -0.0])),
                };
            }

            if (rng.Next(3) == 0)
            {
                key = key with { SpatialIn = AnyDouble(rng), SpatialOut = rng.Next(2) == 0 ? null : AnyDouble(rng) };
            }

            parameter.Keyframes.Add(key);
            at += rng.NextInt64(1, Time.TicksPerSecond * 1000);
        }

        return parameter;
    }

    /// <summary>Un valor que el validador acepta; los límites infinitos (sin tope) se cambian por el extremo finito.</summary>
    private static double InRange(Random rng, ParameterDefinition p)
    {
        double min = Math.Max(p.Minimum, -double.MaxValue);
        double max = Math.Min(p.Maximum, double.MaxValue);
        return rng.Next(5) switch
        {
            0 => min,
            1 => max,
            2 => p.DefaultValue,
            3 => Math.Clamp(AnyDouble(rng), min, max),
            _ => Math.Clamp(min + (rng.NextDouble() * (max - min)), min, max),
        };
    }

    /// <summary>Un número finito: de la lista difícil o con bits al azar (cualquier exponente y mantisa).</summary>
    private static double AnyDouble(Random rng)
    {
        if (rng.Next(2) == 0)
        {
            return Pick(rng, HardDoubles);
        }

        double value;
        do
        {
            value = BitConverter.Int64BitsToDouble(rng.NextInt64(long.MinValue, long.MaxValue));
        }
        while (!double.IsFinite(value));

        return value;
    }

    private static Time AnyNonNegativeTime(Random rng) => rng.Next(4) switch
    {
        0 => Time.Zero,
        1 => Time.FromTicks(long.MaxValue),
        2 => Time.FromTicks(rng.NextInt64(0, long.MaxValue)),
        _ => Time.FromTicks(rng.NextInt64(0, Time.TicksPerSecond * 3600)),
    };

    private static string AnyText(Random rng) => rng.Next(5) == 0 ? Pick(rng, ControlTexts) : Pick(rng, SafeTexts);

    private static T Pick<T>(Random rng, IReadOnlyList<T> items) => items[rng.Next(items.Count)];
}
