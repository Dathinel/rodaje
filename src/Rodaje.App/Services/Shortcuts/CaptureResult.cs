namespace Rodaje.App.Services.Shortcuts;

/// <summary>Qué hacer con una tecla durante la captura y el texto que se le muestra a la persona.</summary>
public readonly record struct CaptureResult(CaptureStatus Status, KeyChord Chord, string Message);
