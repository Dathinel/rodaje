namespace FotoToma.Core.Editing;

/// <summary>
/// Una edición que no se puede hacer: el mensaje, en español, explica por qué en lenguaje simple
/// (por ejemplo «el clip llegaría más allá del final del archivo»). La interfaz lo muestra tal cual.
/// </summary>
public sealed class EditException : Exception
{
    public EditException()
    {
    }

    public EditException(string message)
        : base(message)
    {
    }

    public EditException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
