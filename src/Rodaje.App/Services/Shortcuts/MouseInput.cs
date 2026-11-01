namespace Rodaje.App.Services.Shortcuts;

/// <summary>Las partes del ratón que pueden lanzar algo. La rueda y su inclinación cuentan como un eje cada una.</summary>
public enum MouseInput
{
    Left,
    Right,
    Middle,

    /// <summary>Botón lateral de atrás.</summary>
    X1,

    /// <summary>Botón lateral de adelante.</summary>
    X2,

    /// <summary>Girar la rueda: arriba y abajo.</summary>
    Wheel,

    /// <summary>Inclinar la rueda a la izquierda o a la derecha (o el desplazamiento horizontal del panel táctil).</summary>
    Tilt,
}
