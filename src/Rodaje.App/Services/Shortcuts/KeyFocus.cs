namespace Rodaje.App.Services.Shortcuts;

/// <summary>Dónde está el foco de teclado cuando llega una tecla.</summary>
public enum KeyFocus
{
    /// <summary>En cualquier otro sitio (un botón, el lienzo, ninguno): las teclas son atajos.</summary>
    Other,

    /// <summary>En un campo de texto: solo pasan las acciones que lo permiten.</summary>
    TextInput,

    /// <summary>En un menú que la persona abrió o recorre con el teclado: las teclas sin Ctrl, Alt ni Win son del menú.</summary>
    KeyboardMenu,

    /// <summary>
    /// En una lista (las fichas de la biblioteca, los marcadores): Supr, Retroceso, Ctrl+A, Ctrl+C y Ctrl+V son de la
    /// lista; Ctrl+Mayús+V sigue pegando en el cursor.
    /// </summary>
    List,
}
