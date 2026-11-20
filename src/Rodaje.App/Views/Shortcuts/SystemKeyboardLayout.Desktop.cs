using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Views.Shortcuts;

/// <summary>En Linux no se lee la distribución activa: las teclas de signos se muestran con sus nombres de fábrica.</summary>
// shortcut: sin la distribución de X11 (XKB) un teclado español ve «[» donde está «'»; leerla con xkbcommon si Daniel lo pide.
internal static class SystemKeyboardLayout
{
    public static IKeyboardLayout? Instance => null;
}