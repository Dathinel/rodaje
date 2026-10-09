namespace Rodaje.Core.Serialization;

/// <summary>El archivo no es un proyecto válido; el mensaje se puede mostrar tal cual al usuario.</summary>
public sealed class ProjectFormatException : Exception
{
    public ProjectFormatException()
        : this("El archivo no es un proyecto de Rodaje válido.")
    {
    }

    public ProjectFormatException(string message)
        : this(message, [], null)
    {
    }

    public ProjectFormatException(string message, Exception? innerException)
        : this(message, [], innerException)
    {
    }

    public ProjectFormatException(string message, IReadOnlyList<string> problems, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(problems);
        Problems = problems;
    }

    /// <summary>Problemas concretos; vacía si el mensaje ya lo dice todo.</summary>
    public IReadOnlyList<string> Problems { get; }
}
