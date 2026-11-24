namespace Rodaje.App.Services.Shortcuts;

/// <summary>Lo que muestra una tecla del dibujo: su tecla virtual, su nombre impreso y las acciones que lanza.</summary>
public sealed record KeyCapState(KeyCap Cap, int VirtualKey, string Label, IReadOnlyList<KeyBinding> Bindings)
{
    /// <summary>La acción que lanza con exactamente esos modificadores, o <c>null</c>.</summary>
    public KeyBinding? For(KeyModifiers modifiers) => Bindings.FirstOrDefault(b => b.Chord.Modifiers == modifiers);
}
