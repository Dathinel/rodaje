namespace FotoToma.Core.Serialization;

/// <summary>
/// El archivo no se pudo leer como proyecto de FotoToma. El mensaje está escrito para mostrarse tal
/// cual a quien usa el programa; <see cref="Problems"/> trae el detalle cuando hay varios.
/// </summary>
public sealed class ProjectFormatException : Exception
{
    public ProjectFormatException()
        : this("El archivo no es un proyecto de FotoToma válido.")
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

    /// <summary>Lista de problemas concretos encontrados; vacía si el mensaje ya lo dice todo.</summary>
    public IReadOnlyList<string> Problems { get; }
}
