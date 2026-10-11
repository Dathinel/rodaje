namespace Rodaje.Modules.Protocol;

/// <summary>Base de los errores del protocolo. <see cref="Code"/> es uno de <see cref="ErrorCodes"/>.</summary>
public class ModuleException : Exception
{
    public ModuleException() : this(ErrorCodes.Internal, "Error del módulo.") { }

    public ModuleException(string message) : this(ErrorCodes.Internal, message) { }

    public ModuleException(string message, Exception innerException) : base(message, innerException) => Code = ErrorCodes.Internal;

    public ModuleException(string code, string message, Exception? innerException = null) : base(message, innerException) => Code = code;

    public string Code { get; }
}

/// <summary>La función del módulo falló (parámetros, memoria, error interno…). La conexión sigue sirviendo.</summary>
public sealed class ModuleCallException : ModuleException
{
    public ModuleCallException() { }

    public ModuleCallException(string message) : base(message) { }

    public ModuleCallException(string message, Exception innerException) : base(message, innerException) { }

    public ModuleCallException(string code, string message) : base(code, message) { }
}

/// <summary>El otro lado mandó algo que no cumple el protocolo. La conexión se cierra.</summary>
public sealed class ModuleProtocolException : ModuleException
{
    public ModuleProtocolException() : this("Mensaje malformado.") { }

    public ModuleProtocolException(string message) : base(ErrorCodes.Malformed, message) { }

    public ModuleProtocolException(string message, Exception innerException) : base(ErrorCodes.Malformed, message, innerException) { }
}

/// <summary>Editor y módulo no comparten ninguna versión del protocolo.</summary>
public sealed class ModuleVersionException : ModuleException
{
    public ModuleVersionException() : this("Versión del protocolo incompatible.") { }

    public ModuleVersionException(string message) : base(ErrorCodes.VersionIncompatible, message) { }

    public ModuleVersionException(string message, Exception innerException) : base(ErrorCodes.VersionIncompatible, message, innerException) { }
}

/// <summary>La conexión terminó (el módulo cerró, se colgó o murió).</summary>
public class ModuleDisconnectedException : ModuleException
{
    public ModuleDisconnectedException() : this("El módulo se desconectó.") { }

    public ModuleDisconnectedException(string message) : base("disconnected", message) { }

    public ModuleDisconnectedException(string message, Exception innerException) : base("disconnected", message, innerException) { }
}

/// <summary>El proceso del módulo terminó; <see cref="ExitCode"/> es su código de salida.</summary>
public sealed class ModuleExitedException : ModuleDisconnectedException
{
    public ModuleExitedException() { }

    public ModuleExitedException(string message) : base(message) { }

    public ModuleExitedException(string message, Exception innerException) : base(message, innerException) { }

    public ModuleExitedException(int exitCode, string? stderrTail)
        : base($"El módulo terminó con el código {exitCode}." + (string.IsNullOrWhiteSpace(stderrTail) ? "" : " " + stderrTail.Trim())) =>
        ExitCode = exitCode;

    public int? ExitCode { get; }
}
