namespace Rodaje.Core.Editing;

/// <summary>Borde de un clip que se recorta.</summary>
public enum TrimEdge
{
    /// <summary>Inicio del clip (punto de entrada).</summary>
    In,

    /// <summary>Final del clip (punto de salida).</summary>
    Out,
}

/// <summary>Qué pasa con lo que ya ocupa el lugar donde se coloca un clip.</summary>
public enum PlacementMode
{
    /// <summary>Si el lugar está ocupado la operación falla.</summary>
    Strict,

    /// <summary>El clip nuevo tapa y recorta lo que había debajo.</summary>
    Overwrite,

    /// <summary>Lo que había desde ese punto se corre a la derecha para hacer espacio (ripple).</summary>
    Insert,
}

/// <summary>Qué pistas se corren cuando una edición en modo ripple cambia la duración.</summary>
public enum RippleScope
{
    /// <summary>Solo las pistas de los clips editados (y de sus enlazados).</summary>
    Track,

    /// <summary>Todas las pistas no bloqueadas, para mantener la sincronía entre capas.</summary>
    AllTracks,
}
