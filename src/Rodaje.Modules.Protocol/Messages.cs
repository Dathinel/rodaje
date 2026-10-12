using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rodaje.Modules.Protocol;

/// <summary>
/// Un mensaje del protocolo. En el cable es un objeto JSON con <c>type</c>; ver <c>docs/fase-7.md</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(InvokeMessage), "invoke")]
[JsonDerivedType(typeof(ProgressMessage), "progress")]
[JsonDerivedType(typeof(ResultMessage), "result")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
[JsonDerivedType(typeof(CancelMessage), "cancel")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(LogMessage), "log")]
[JsonDerivedType(typeof(ShutdownMessage), "shutdown")]
public abstract record ModuleMessage
{
    internal static readonly HashSet<string> KnownTypes =
        ["hello", "welcome", "invoke", "progress", "result", "error", "cancel", "ping", "pong", "log", "shutdown"];

    /// <summary>El motivo por el que el mensaje no es válido, o <c>null</c>.</summary>
    internal virtual string? Validate() => null;
}

/// <summary>Editor → módulo, lo primero: el rango de versiones que entiende.</summary>
public sealed record HelloMessage(int MinProtocol, int MaxProtocol, string App, string AppVersion, string? Culture = null) : ModuleMessage
{
    internal override string? Validate() =>
        MinProtocol < 1 || MaxProtocol < MinProtocol ? "rango de protocolo inválido"
        : Limits.Text(App, Limits.MaxId) ?? Limits.Text(AppVersion, Limits.MaxId) ?? Limits.Text(Culture, Limits.MaxId, optional: true);
}

/// <summary>Módulo → editor: la versión elegida y lo que ofrece.</summary>
public sealed record WelcomeMessage(int Protocol, ModuleInfo Module) : ModuleMessage
{
    internal override string? Validate() => Protocol < 1 ? "versión inválida" : Module.Validate();
}

public sealed record ModuleInfo(string Id, string Version, string Name, IReadOnlyList<FunctionInfo> Functions, string? Device = null)
{
    internal string? Validate()
    {
        if (Limits.Identifier(Id) is { } id) return id;
        if ((Limits.Text(Version, Limits.MaxId) ?? Limits.Text(Name, Limits.MaxName) ?? Limits.Text(Device, Limits.MaxName, optional: true)) is { } t) return t;
        if (Functions.Count > Limits.MaxFunctions) return "demasiadas funciones";
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (FunctionInfo f in Functions)
        {
            if (f is null) return "función nula";
            if (f.Validate() is { } e) return e;
            if (!seen.Add(f.Id)) return $"función repetida: {f.Id}";
        }
        return null;
    }
}

/// <summary>Una función del módulo. <paramref name="Inputs"/> y <paramref name="Outputs"/> son tipos de búfer (<c>video</c>, <c>audio</c>, <c>bytes</c>).</summary>
public sealed record FunctionInfo(string Id, string Name, bool Cancellable = true, bool ReportsProgress = true,
    IReadOnlyList<string>? Inputs = null, IReadOnlyList<string>? Outputs = null)
{
    internal string? Validate()
    {
        if ((Limits.Identifier(Id) ?? Limits.Text(Name, Limits.MaxName)) is { } e) return e;
        foreach (IReadOnlyList<string>? list in (IReadOnlyList<string>?[])[Inputs, Outputs])
        {
            if (list is null) continue;
            if (list.Count > Limits.MaxBuffers) return "demasiados búferes";
            foreach (string s in list)
                if (Limits.Identifier(s) is { } k) return k;
        }
        return null;
    }
}

/// <summary>Editor → módulo: ejecutar una función. <paramref name="Id"/> lo elige el editor y es único en la conexión.</summary>
public sealed record InvokeMessage(long Id, string Function, JsonElement? Parameters = null, IReadOnlyList<SharedBufferRef>? Buffers = null) : ModuleMessage
{
    internal override string? Validate() => Limits.RequestId(Id) ?? Limits.Identifier(Function) ?? Limits.Json(Parameters) ?? Limits.BufferList(Buffers);
}

/// <summary>Módulo → editor: avance de 0 a 1 y la etapa en curso.</summary>
public sealed record ProgressMessage(long Id, double Fraction, string? Stage = null) : ModuleMessage
{
    internal override string? Validate() =>
        Limits.RequestId(Id) ?? (double.IsFinite(Fraction) && Fraction is >= 0 and <= 1 ? Limits.Text(Stage, Limits.MaxName, optional: true) : "avance fuera de 0–1");
}

/// <summary>Módulo → editor: la función terminó. Los búferes devueltos están en regiones que el editor le pasó.</summary>
public sealed record ResultMessage(long Id, JsonElement? Value = null, IReadOnlyList<SharedBufferRef>? Buffers = null) : ModuleMessage
{
    internal override string? Validate() => Limits.RequestId(Id) ?? Limits.Json(Value) ?? Limits.BufferList(Buffers);
}

/// <summary>Error de una llamada (<paramref name="Id"/>) o de la conexión (sin id). <paramref name="Code"/> está en <see cref="ErrorCodes"/>.</summary>
public sealed record ErrorMessage(string Code, string Message, long? Id = null) : ModuleMessage
{
    internal override string? Validate() =>
        (Id is { } id ? Limits.RequestId(id) : null) ?? Limits.Identifier(Code) ?? Limits.Text(Message, Limits.MaxText);
}

public sealed record CancelMessage(long Id) : ModuleMessage
{
    internal override string? Validate() => Limits.RequestId(Id);
}

public sealed record PingMessage(long Id) : ModuleMessage;

public sealed record PongMessage(long Id) : ModuleMessage;

/// <summary>Módulo → editor: una línea para el registro (<c>info</c>, <c>warn</c>, <c>error</c>).</summary>
public sealed record LogMessage(string Level, string Text) : ModuleMessage
{
    internal override string? Validate() => Limits.Identifier(Level) ?? Limits.Text(Text, Limits.MaxText);
}

/// <summary>Editor → módulo: cancelar todo, liberar la GPU y salir.</summary>
public sealed record ShutdownMessage : ModuleMessage;

/// <summary>Un búfer dentro de una región de memoria compartida (<see cref="SharedMemoryRegion"/>).</summary>
public sealed record SharedBufferRef(string Region, long Offset, long Length, VideoLayout? Video = null, AudioLayout? Audio = null)
{
    internal string? Validate()
    {
        if (!Limits.RegionName(Region)) return "nombre de región inválido";
        if (Offset < 0 || Length <= 0 || Length > int.MaxValue || Offset > long.MaxValue - Length) return "búfer fuera de rango";
        if (Video is not null && Audio is not null) return "un búfer es video o audio, no los dos";
        if (Video is { } v)
        {
            int bpp = PixelFormats.BytesPerPixel(v.PixelFormat);
            if (bpp == 0) return $"formato de píxel desconocido: {v.PixelFormat}";
            if (v.Width is < 1 or > 16384 || v.Height is < 1 or > 16384) return "tamaño de cuadro inválido";
            if (v.Stride < (long)v.Width * bpp || (long)v.Stride * v.Height > Length) return "el cuadro no cabe en el búfer";
        }
        if (Audio is { } a)
        {
            if (a.SampleFormat != SampleFormats.F32) return $"formato de muestra desconocido: {a.SampleFormat}";
            if (a.SampleRate is < 8000 or > 384000 || a.Channels is < 1 or > 32 || a.Frames < 0) return "audio inválido";
            if (a.Frames * a.Channels * 4L > Length) return "el audio no cabe en el búfer";
        }
        return null;
    }
}

public sealed record VideoLayout(int Width, int Height, int Stride, string PixelFormat);

/// <summary>Audio intercalado.</summary>
public sealed record AudioLayout(int SampleRate, int Channels, long Frames, string SampleFormat = SampleFormats.F32);

public static class PixelFormats
{
    public const string Rgba8 = "rgba8";
    public const string Bgra8 = "bgra8";
    public const string Rgba16F = "rgba16f";
    public const string Gray8 = "gray8";

    /// <summary>0 si no se conoce.</summary>
    public static int BytesPerPixel(string format) => format switch
    {
        Rgba8 or Bgra8 => 4,
        Rgba16F => 8,
        Gray8 => 1,
        _ => 0,
    };
}

public static class SampleFormats
{
    public const string F32 = "f32";
}

/// <summary>Códigos de error estables.</summary>
public static class ErrorCodes
{
    public const string VersionIncompatible = "version-incompatible";
    public const string UnknownFunction = "unknown-function";
    public const string InvalidParameters = "invalid-parameters";
    public const string Cancelled = "cancelled";
    public const string Busy = "busy";
    public const string OutOfMemory = "out-of-memory";
    public const string BufferOutOfRange = "buffer-out-of-range";
    public const string Malformed = "malformed";
    public const string Internal = "internal";
}

internal static class Limits
{
    public const int MaxId = 64;
    public const int MaxName = 200;
    public const int MaxText = 4096;
    public const int MaxFunctions = 128;
    public const int MaxBuffers = 16;

    public static string? Identifier(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > MaxId) return "identificador vacío o largo";
        bool prevSep = true;
        foreach (char c in s)
        {
            bool sep = c is '.' or '-';
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || sep) || (sep && prevSep)) return $"identificador inválido: {Clip(s)}";
            prevSep = sep;
        }
        return prevSep ? $"identificador inválido: {Clip(s)}" : null;
    }

    public static string? Text(string? s, int max, bool optional = false) =>
        s is null ? (optional ? null : "falta un texto") : s.Length > max ? "texto demasiado largo" : null;

    public static bool RegionName(string? name) =>
        name is { Length: > 0 and <= 128 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    public static string? RequestId(long id) => id > 0 ? null : "id de llamada inválido";

    public static string? BufferList(IReadOnlyList<SharedBufferRef>? list)
    {
        if (list is null) return null;
        if (list.Count > MaxBuffers) return "demasiados búferes";
        foreach (SharedBufferRef b in list)
            if ((b is null ? "búfer nulo" : b.Validate()) is { } e) return e;
        return null;
    }

    /// <summary>
    /// UTF-8 estricto (D-031) también dentro de <c>parameters</c> y <c>value</c>: el JSON puede traer escapes de un
    /// sustituto UTF-16 suelto (<c>"\ud800"</c>) que el lector acepta dentro de un <see cref="JsonElement"/> pero que no
    /// se puede convertir a texto ni volver a escribir. Se revisan todos los textos y nombres de propiedad.
    /// </summary>
    public static string? Json(JsonElement? element)
    {
        if (element is not { } e) return null;
        try
        {
            ReadAllText(e);
            return null;
        }
        catch (InvalidOperationException)
        {
            return "texto con un sustituto UTF-16 suelto";
        }
    }

    // GetString lanza InvalidOperationException con un sustituto suelto; el anidamiento ya está limitado a 32 niveles.
    private static void ReadAllText(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                _ = e.GetString();
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in e.EnumerateArray()) ReadAllText(item);
                break;
            case JsonValueKind.Object:
                foreach (JsonProperty property in e.EnumerateObject())
                {
                    _ = property.Name;
                    ReadAllText(property.Value);
                }
                break;
        }
    }

    private static string Clip(string s) => s.Length > 32 ? s[..32] + "…" : s;
}
