using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;
using Windows.System;

namespace Rodaje.App.Views.Shortcuts;

/// <summary>
/// El ratón de una zona (línea de tiempo, visor, biblioteca): los botones central y laterales lanzan su acción del
/// <see cref="MouseMap"/> por el <see cref="ShortcutRouter"/>, y la rueda hace la suya. Las acciones que dependen de la
/// vista (zoom, desplazar, arrastrar) las resuelve la vista con <see cref="Resolve"/> o con el callback de la rueda.
/// </summary>
internal sealed partial class MouseShortcutHost
{
    private readonly MouseContext _context;
    private readonly EditorServices _services;
    private readonly Func<string, MouseInput, int, bool>? _axis;

    private MouseShortcutHost(UIElement element, MouseContext context, EditorServices services, Func<string, MouseInput, int, bool>? axis, bool wheel)
    {
        _context = context;
        _services = services;
        _axis = axis;
        element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), handledEventsToo: true);
        if (wheel)
        {
            element.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnWheel), handledEventsToo: false);
        }
    }

    /// <summary>
    /// Une el ratón de la zona con <paramref name="element"/>. Con <paramref name="wheel"/>, la rueda también pasa por
    /// aquí: «Recorrer cuadros» se resuelve solo y el resto se le pregunta a <paramref name="axis"/> (acción, parte y
    /// pasos con signo: positivo es abajo o a la derecha).
    /// </summary>
    public static MouseShortcutHost Attach(UIElement element, MouseContext context, EditorServices services, bool wheel = false, Func<string, MouseInput, int, bool>? axis = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(services);
        return new MouseShortcutHost(element, context, services, axis, wheel);
    }

    /// <summary>La acción de esa parte del ratón con esos modificadores, en la zona.</summary>
    public static string? Resolve(ShortcutRegistry registry, MouseContext context, MouseInput input, VirtualKeyModifiers modifiers) =>
        registry.Mouse.Get(context, new MouseChord(input, Modifiers(modifiers)));

    public static KeyModifiers Modifiers(VirtualKeyModifiers modifiers) =>
        (modifiers.HasFlag(VirtualKeyModifiers.Control) ? KeyModifiers.Control : KeyModifiers.None)
        | (modifiers.HasFlag(VirtualKeyModifiers.Shift) ? KeyModifiers.Shift : KeyModifiers.None)
        | (modifiers.HasFlag(VirtualKeyModifiers.Menu) ? KeyModifiers.Alt : KeyModifiers.None);

    /// <summary>El botón que acaba de bajar, si es uno de los que se pueden reasignar.</summary>
    public static MouseInput? PressedButton(PointerPointProperties properties) => properties.PointerUpdateKind switch
    {
        PointerUpdateKind.MiddleButtonPressed => MouseInput.Middle,
        PointerUpdateKind.XButton1Pressed => MouseInput.X1,
        PointerUpdateKind.XButton2Pressed => MouseInput.X2,
        _ => null,
    };

    /// <summary>Pasos con signo de la rueda: positivo es hacia abajo (o a la derecha si se inclina).</summary>
    public static int Steps(PointerPointProperties properties)
    {
        int delta = properties.MouseWheelDelta;
        int steps = Math.Max(1, Math.Abs(delta) / 120);
        bool forward = properties.IsHorizontalMouseWheel ? delta > 0 : delta < 0;
        return forward ? steps : -steps;
    }

    /// <summary>«Recorrer cuadros»: un cuadro por paso, atrás si es negativo.</summary>
    public static void StepFrames(ShortcutRouter router, int steps)
    {
        string action = steps < 0 ? ActionIds.PreviousFrame : ActionIds.NextFrame;
        for (int i = 0; i < Math.Abs(steps); i++)
        {
            router.Invoke(action);
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(sender as UIElement);
        if (PressedButton(point.Properties) is not { } input
            || Resolve(_services.ShortcutRegistry, _context, input, e.KeyModifiers) is not { } actionId
            || MouseGestures.IsGesture(actionId))
        {
            return;
        }

        e.Handled = _services.Shortcuts.Invoke(actionId) || e.Handled;
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(sender as UIElement);
        MouseInput input = point.Properties.IsHorizontalMouseWheel ? MouseInput.Tilt : MouseInput.Wheel;
        if (Resolve(_services.ShortcutRegistry, _context, input, e.KeyModifiers) is not { } actionId)
        {
            return;
        }

        int steps = Steps(point.Properties);
        if (actionId == MouseGestures.Frames)
        {
            StepFrames(_services.Shortcuts, steps);
            e.Handled = true;
        }
        else if (_axis?.Invoke(actionId, input, steps) == true)
        {
            e.Handled = true;
        }
    }
}
