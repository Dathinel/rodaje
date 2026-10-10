using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Rodaje.App.Services.Shortcuts;
using Windows.System;
using Windows.UI.Core;

namespace Rodaje.App.Shell;

/// <summary>
/// Atajos de toda la ventana: escucha el túnel (<c>PreviewKeyDown</c>) en la raíz, antes que cualquier control, y pasa
/// cada combinación a <see cref="ShortcutRouter"/>. Si es un atajo, la marca como manejada también al soltarla, porque
/// un botón con el foco se activa con Espacio al soltar (docs/sondeo-winui.md, trampa 6).
/// </summary>
internal sealed partial class KeyboardShortcutHost : IDisposable
{
    private readonly UIElement _root;
    private readonly ShortcutRouter _router;
    private readonly Func<KeyChord, bool>? _intercept;
    private readonly HashSet<VirtualKey> _consumed = [];

    /// <param name="intercept">Se consulta antes que los atajos (por ejemplo, Esc cierra una página); <c>true</c> la consume.</param>
    public KeyboardShortcutHost(UIElement root, ShortcutRouter router, Func<KeyChord, bool>? intercept = null)
    {
        _root = root;
        _router = router;
        _intercept = intercept;
        _root.PreviewKeyDown += OnPreviewKeyDown;
        _root.PreviewKeyUp += OnPreviewKeyUp;
    }

    /// <summary>Hora (<see cref="System.Diagnostics.Stopwatch"/>) del último KeyDown que llegó a la ventana, para medir.</summary>
    public long LastKeyDownTimestamp { get; private set; }

    public static KeyModifiers CurrentModifiers()
    {
        var modifiers = KeyModifiers.None;
        if (IsDown(VirtualKey.Control))
        {
            modifiers |= KeyModifiers.Control;
        }

        if (IsDown(VirtualKey.Shift))
        {
            modifiers |= KeyModifiers.Shift;
        }

        if (IsDown(VirtualKey.Menu))
        {
            modifiers |= KeyModifiers.Alt;
        }

        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
        {
            modifiers |= KeyModifiers.Windows;
        }

        return modifiers;
    }

    public void Dispose()
    {
        _root.PreviewKeyDown -= OnPreviewKeyDown;
        _root.PreviewKeyUp -= OnPreviewKeyUp;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (KeyChord.IsModifierKey((int)e.Key))
        {
            return;
        }

        LastKeyDownTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        var chord = new KeyChord((int)e.Key, CurrentModifiers());
        bool handled = _intercept?.Invoke(chord) == true
            || _router.Dispatch(chord, e.KeyStatus.WasKeyDown, IsTextInputFocused());
        if (handled)
        {
            _consumed.Add(e.Key);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_consumed.Remove(e.Key))
        {
            e.Handled = true;
        }
    }

    private bool IsTextInputFocused() =>
        _root.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or NumberBox;
}
