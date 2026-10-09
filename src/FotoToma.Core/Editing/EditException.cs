namespace FotoToma.Core.Editing;

/// <summary>Edición imposible; el mensaje se puede mostrar tal cual al usuario.</summary>
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
