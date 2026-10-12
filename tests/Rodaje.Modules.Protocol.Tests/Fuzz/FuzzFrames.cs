using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>Quién manda el marco: cambia qué tipos de mensaje salen más.</summary>
internal enum FuzzDirection
{
    ToEditor,
    ToModule,
}

/// <summary>
/// Generador determinista de mensajes y marcos hostiles. La mayoría son mensajes casi válidos (tipos y campos del
/// protocolo con valores en el borde de los límites, ids repetidos o ajenos, versiones raras, búferes fuera de rango); el
/// resto son mutaciones de bytes, JSON que no es objeto, UTF-8 inválido, anidado profundo y marcos con el largo mal.
/// </summary>
internal sealed class FuzzFrames(Random random, long maxId = 4)
{
    private static readonly string[] ToEditorTypes = ["result", "result", "progress", "progress", "error", "error", "log", "pong", "welcome", "invoke", "ping", "hello"];
    private static readonly string[] ToModuleTypes = ["invoke", "invoke", "invoke", "cancel", "cancel", "ping", "hello", "result", "error", "progress", "log", "welcome"];
    private static readonly string[] Functions = ["eco", "contar", "fallar", "copiar", "no-existe", "Eco", "", "eco\\u0000", "../eco", "a..b", "-eco", "eco-"];
    private static readonly string[] Codes = ["internal", "busy", "cancelled", "unknown-function", "buffer-out-of-range", "malformed", "version-incompatible", "futuro", "", "Mal", "a b"];

    public Random Random { get; } = random;

    /// <summary>Un marco: casi siempre con su largo bien puesto; a veces con el encabezado roto.</summary>
    public byte[] Frame(FuzzDirection direction)
    {
        byte[] payload = Payload(direction);
        if (Random.Next(12) != 0) return FuzzSupport.Frame(payload);
        byte[] header = new byte[4];
        int length = Random.Next(7) switch
        {
            0 => 0,
            1 => -1,
            2 => int.MinValue,
            3 => ProtocolVersion.MaxFrameBytes + 1,
            4 => payload.Length + Random.Next(1, 64),
            5 => Math.Max(1, payload.Length - Random.Next(1, 8)),
            _ => int.MaxValue,
        };
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        return [.. header, .. payload];
    }

    /// <summary>
    /// Un marco con un mensaje que el protocolo acepta (pasa <see cref="MessageChannel.Parse"/>) pero con valores al azar:
    /// ids ajenos o repetidos, avances, registros, errores. Sirve para llegar más hondo que con basura, que corta enseguida.
    /// </summary>
    public byte[] ValidFrame(FuzzDirection direction)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            byte[] payload = Encoding.UTF8.GetBytes(Message(direction));
            try
            {
                if (MessageChannel.Parse(payload) is not null) return FuzzSupport.Frame(payload);
            }
            catch (ModuleProtocolException)
            {
            }
        }
        return FuzzSupport.Frame("{\"type\":\"log\",\"level\":\"info\",\"text\":\"nada\"}"u8.ToArray());
    }

    /// <summary>El JSON de un mensaje, sin marco.</summary>
    public byte[] Payload(FuzzDirection direction)
    {
        int pick = Random.Next(100);
        if (pick < 72) return Encoding.UTF8.GetBytes(Message(direction));
        if (pick < 87) return Mutate(Encoding.UTF8.GetBytes(Message(direction)));
        if (pick < 91) return Bytes(Random.Next(1, 64));
        if (pick < 95) return Encoding.UTF8.GetBytes(Pick("[]", "null", "1", "\"ping\"", "{}", "{\"type\":null}", "{\"type\":7}", "{\"type\":\"\"}", "﻿{\"type\":\"ping\",\"id\":1}", " ", "{\"type\":\"ping\",\"id\":1} basura"));
        if (pick < 98) return InvalidUtf8(direction);
        return Huge(direction);
    }

    /// <summary>Un mensaje JSON casi válido: tipo del protocolo (o desconocido) y campos con valores al azar.</summary>
    public string Message(FuzzDirection direction)
    {
        string type = Random.Next(15) == 0
            ? Pick("futuro", "Invoke", "ping ", "shutdown", "x-y", "a".PadRight(70, 'a'))
            : Pick(direction == FuzzDirection.ToEditor ? ToEditorTypes : ToModuleTypes);
        List<string> fields = type switch
        {
            "hello" => [P("minProtocol", Version()), P("maxProtocol", Version()), P("app", Text(64)), P("appVersion", Text(64)), Maybe(P("culture", Text(64)))],
            "welcome" => [P("protocol", Version()), P("module", Module())],
            "invoke" => [P("id", Id()), P("function", Q(Pick(Functions))), Maybe(P("parameters", Value(0))), Maybe(P("buffers", Buffers()))],
            "progress" => [P("id", Id()), P("fraction", Fraction()), Maybe(P("stage", Text(200)))],
            "result" => [P("id", Id()), Maybe(P("value", Value(0))), Maybe(P("buffers", Buffers()))],
            "error" => [Maybe(P("id", Id())), P("code", Q(Pick(Codes))), P("message", Text(4096))],
            "cancel" or "ping" or "pong" => [P("id", Id())],
            "log" => [P("level", Q(Pick("info", "warn", "error", "Info", ""))), P("text", Text(4096))],
            _ => [],
        };
        fields.RemoveAll(f => f.Length == 0);
        // Campos que faltan, sobran, se repiten o vienen antes que «type».
        if (fields.Count > 0 && Random.Next(10) == 0) fields.RemoveAt(Random.Next(fields.Count));
        if (Random.Next(8) == 0) fields.Add(P(Pick("nuevo", "x", "type", "id", "ID"), Value(0)));
        if (fields.Count > 0 && Random.Next(12) == 0) fields.Add(fields[Random.Next(fields.Count)]);
        string typeField = P("type", Q(type));
        if (Random.Next(4) == 0) fields.Insert(Random.Next(fields.Count + 1), typeField);
        else fields.Insert(0, typeField);
        return "{" + string.Join(",", fields) + "}";
    }

    /// <summary>Ids: los de las llamadas en curso (1…maxId) casi siempre, y valores fuera de rango o de otro tipo.</summary>
    public string Id() => Random.Next(10) switch
    {
        < 7 => Random.NextInt64(1, maxId + 1).ToString(CultureInfo.InvariantCulture),
        7 => Pick("0", "-1", "9223372036854775807", "-9223372036854775808", "9223372036854775808", "1e3", "1.5", "\"1\"", "null", "true", "[1]", "1e400"),
        _ => Random.NextInt64(1, long.MaxValue).ToString(CultureInfo.InvariantCulture),
    };

    private string Version() => Random.Next(6) switch
    {
        < 3 => Random.Next(-1, 4).ToString(CultureInfo.InvariantCulture),
        3 => Pick("2147483647", "-2147483648", "2147483648", "1.0", "1.5", "\"1\"", "null", "1e2", "01"),
        _ => "1",
    };

    private string Fraction() => Random.Next(5) switch
    {
        < 3 => Random.NextDouble().ToString("R", CultureInfo.InvariantCulture),
        3 => Pick("0", "1", "-0.0001", "1.0000001", "1e308", "-1e308", "\"NaN\"", "NaN", "1e400", "null"),
        _ => Pick("0.5", "1"),
    };

    private string Module()
    {
        int count = Random.Next(10) == 0 ? Random.Next(126, 131) : Random.Next(0, 4);
        List<string> functions = [];
        for (int i = 0; i < count; i++)
            functions.Add(Random.Next(30) == 0 ? "null" : "{" + P("id", Q(Random.Next(20) == 0 ? "eco" : "f" + i.ToString(CultureInfo.InvariantCulture))) + "," + P("name", Text(200)) + "}");
        return "{" + string.Join(",", new[]
        {
            P("id", Q(Pick("hostil", "Hostil", "", "a.b-c", "-x"))),
            P("version", Text(64)),
            P("name", Text(200)),
            Random.Next(10) == 0 ? P("functions", "null") : P("functions", "[" + string.Join(",", functions) + "]"),
            Random.Next(5) == 0 ? P("device", Text(200)) : "",
        }.Where(f => f.Length > 0)) + "}";
    }

    /// <summary>Lista de búferes: regiones ajenas o con nombres raros, desplazamientos y largos fuera de rango, formatos raros.</summary>
    private string Buffers()
    {
        int count = Random.Next(20) == 0 ? 17 : Random.Next(0, 4);
        List<string> list = [];
        for (int i = 0; i < count; i++) list.Add(Random.Next(25) == 0 ? "null" : Buffer(Q(Pick("Rodaje.Mod.ajena", "Rodaje.Mod.abc", "r", "", "Local\\\\x", "a b", new string('r', 129)))));
        return "[" + string.Join(",", list) + "]";
    }

    /// <summary>Un búfer en la región <paramref name="region"/> (ya entre comillas).</summary>
    public string Buffer(string region, long capacity = 1 << 20)
    {
        List<string> fields = [P("region", region), P("offset", Offset(capacity)), P("length", Offset(capacity))];
        int layout = Random.Next(6);
        if (layout == 0)
            fields.Add(P("video", "{" + string.Join(",", P("width", Dim()), P("height", Dim()), P("stride", Dim()), P("pixelFormat", Q(Pick("rgba8", "bgra8", "rgba16f", "gray8", "yuv9", ""))) + "}")));
        else if (layout == 1)
            fields.Add(P("audio", "{" + string.Join(",", P("sampleRate", Pick("48000", "7999", "384001", "0")), P("channels", Pick("2", "0", "33", "-1")), P("frames", Offset(capacity)), P("sampleFormat", Q(Pick("f32", "s16")))) + "}"));
        else if (layout == 2 && Random.Next(4) == 0)
            fields.Add(P("video", "null"));
        return "{" + string.Join(",", fields) + "}";
    }

    private string Offset(long capacity) => Random.Next(8) switch
    {
        < 4 => Random.NextInt64(0, capacity + 1).ToString(CultureInfo.InvariantCulture),
        4 => Random.NextInt64(capacity - 16, capacity + 16).ToString(CultureInfo.InvariantCulture),
        5 => Pick("0", "-1", "2147483647", "2147483648", "9223372036854775807", "9223372036854775000", "-9223372036854775808"),
        _ => Random.Next(1, 4096).ToString(CultureInfo.InvariantCulture),
    };

    private string Dim() => Pick("1", "4", "16", "1920", "16384", "16385", "0", "-1", "2147483647", "65536");

    /// <summary>Un texto con largo en el borde del límite <paramref name="max"/> o con caracteres raros.</summary>
    private string Text(int max) => Random.Next(10) switch
    {
        0 => Q(new string('x', max)),
        1 => Q(new string('x', max + 1)),
        2 => Q("\\ud800 suelto"),
        3 => Q("ñandú 🎬 \\u0000 \\u202e"),
        4 => "null",
        5 => Pick("1", "[]", "{}"),
        _ => Q("texto"),
    };

    /// <summary>Un valor JSON cualquiera; a veces anidado por encima del límite de 32.</summary>
    private string Value(int depth)
    {
        if (depth == 0 && Random.Next(20) == 0)
        {
            int deep = Random.Next(28, 40);
            return new string('[', deep) + new string(']', deep);
        }
        return (depth > 3 ? Random.Next(6) : Random.Next(8)) switch
        {
            0 => Pick("null", "true", "false"),
            1 => Pick("0", "-0", "1e400", "-1e-400", "123456789012345678901234567890", "1.5", "NaN"),
            2 => Q("cadena"),
            3 => Text(100),
            4 => Random.Next(-1000, 1000).ToString(CultureInfo.InvariantCulture),
            5 => Q(new string('<', Random.Next(0, 300))),
            6 => "[" + string.Join(",", Enumerable.Range(0, Random.Next(4)).Select(_ => Value(depth + 1))) + "]",
            _ => "{" + string.Join(",", Enumerable.Range(0, Random.Next(4)).Select(i => P("k" + i.ToString(CultureInfo.InvariantCulture), Value(depth + 1)))) + "}",
        };
    }

    private byte[] Mutate(byte[] data)
    {
        if (data.Length == 0) return [0];
        List<byte> list = [.. data];
        for (int k = Random.Next(1, 5); k > 0 && list.Count > 0; k--)
        {
            int at = Random.Next(list.Count);
            switch (Random.Next(4))
            {
                case 0:
                    list[at] = (byte)Random.Next(256);
                    break;
                case 1:
                    list.RemoveAt(at);
                    break;
                case 2:
                    list.Insert(at, (byte)Pick('{', '}', '[', ']', '"', ',', ':', '\\', '\0', 0xFF));
                    break;
                default:
                    list.RemoveRange(at, list.Count - at);
                    break;
            }
        }
        return list.Count == 0 ? [(byte)'{'] : [.. list];
    }

    private byte[] InvalidUtf8(FuzzDirection direction)
    {
        byte[] message = Encoding.UTF8.GetBytes(Message(direction));
        int quote = Array.LastIndexOf(message, (byte)'"');
        byte[] bad = Pick<byte[]>([0xC3, 0x28], [0xFF], [0xED, 0xA0, 0x80], [0xF4, 0x90, 0x80, 0x80], [0xC0, 0xAF], [0xE2, 0x82]);
        return quote < 1 ? [.. message, .. bad] : [.. message[..quote], .. bad, .. message[quote..]];
    }

    /// <summary>Un mensaje cerca del máximo de 1 MiB (por debajo o justo por encima).</summary>
    private byte[] Huge(FuzzDirection direction)
    {
        string type = direction == FuzzDirection.ToEditor ? "log" : "invoke";
        string prefix = type == "log" ? "{\"type\":\"log\",\"level\":\"info\",\"text\":\"" : "{\"type\":\"invoke\",\"id\":1,\"function\":\"eco\",\"parameters\":\"";
        int target = ProtocolVersion.MaxFrameBytes + Random.Next(-8, 9);
        int fill = Math.Max(0, target - prefix.Length - 2);
        return Encoding.UTF8.GetBytes(prefix + new string('a', fill) + "\"}");
    }

    private byte[] Bytes(int count)
    {
        byte[] data = new byte[count];
        Random.NextBytes(data);
        return data;
    }

    private string Maybe(string field) => Random.Next(2) == 0 ? field : "";

    private static string P(string name, string value) => "\"" + name + "\":" + value;

    private static string Q(string text) => "\"" + text + "\"";

    private T Pick<T>(params T[] options) => options[Random.Next(options.Length)];
}
