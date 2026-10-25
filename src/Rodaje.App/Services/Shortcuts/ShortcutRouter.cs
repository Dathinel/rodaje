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

    /// <summary>En una lista (las fichas de la biblioteca, los marcadores): Supr, Retroceso y Ctrl+A son de la lista.</summary>
    List,
}

/// <summary>
/// Lanza las acciones: une cada identificador del <see cref="ShortcutRegistry"/> con quien sabe ejecutarlo y decide
/// qué hacer con cada combinación que llega de la ventana. Los menús y botones también pasan por aquí
/// (<see cref="Invoke"/>), así una acción se comporta igual venga de donde venga.
/// </summary>
/// <remarks>
/// Una acción puede tener varios ejecutores; gana el último que se unió y que hoy puede ejecutarla. Así una vista con el
/// foco (por ejemplo, la biblioteca para Supr) se adelanta a la línea de tiempo sin que ninguna conozca a la otra.
/// </remarks>
public sealed partial class ShortcutRouter
{
    private static readonly KeyChord[] ListKeys = [KeyChord.Parse("Supr"), KeyChord.Parse("Retroceso"), KeyChord.Parse("Ctrl+A")];

    private readonly Dictionary<string, List<Binding>> _bindings = new(StringComparer.Ordinal);
    private readonly List<Func<ShortcutAction, bool>> _restrictions = [];
    private int _suspended;

    public ShortcutRouter(ShortcutRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Registry = registry;
    }

    public ShortcutRegistry Registry { get; }

    /// <summary>Se lanzó una acción, desde el teclado o desde un menú.</summary>
    public event EventHandler<string>? Invoked;

    /// <summary>Mientras está suspendido, las teclas no lanzan nada (por ejemplo, al capturar una tecla nueva).</summary>
    public bool IsSuspended => _suspended > 0;

    /// <summary>Une la acción con quien la ejecuta. Se deshace con el <see cref="IDisposable"/> devuelto.</summary>
    /// <param name="canExecute">Si da <c>false</c>, se prueba con el ejecutor anterior y la tecla sigue su camino.</param>
    public IDisposable Bind(string actionId, Action execute, Func<bool>? canExecute = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentNullException.ThrowIfNull(execute);
        if (!_bindings.TryGetValue(actionId, out List<Binding>? list))
        {
            _bindings[actionId] = list = [];
        }

        var binding = new Binding(this, actionId, execute, canExecute);
        list.Add(binding);
        return binding;
    }

    public bool CanInvoke(string actionId) => FindHandler(actionId) is not null;

    /// <summary>Ejecuta la acción si alguien puede; <c>true</c> si se ejecutó.</summary>
    public bool Invoke(string actionId)
    {
        if (FindHandler(actionId) is not { } binding)
        {
            return false;
        }

        binding.Execute();
        Invoked?.Invoke(this, actionId);
        return true;
    }

    /// <summary>Como <see cref="Dispatch(KeyChord, bool, KeyFocus)"/>, con el foco en un campo de texto o no.</summary>
    public bool Dispatch(KeyChord chord, bool isRepeat, bool isTextInputFocused) =>
        Dispatch(chord, isRepeat, isTextInputFocused ? KeyFocus.TextInput : KeyFocus.Other);

    /// <summary>
    /// Una tecla de la ventana. <c>true</c> si es un atajo y se consumió: la ventana debe marcarla como manejada para
    /// que ningún control (por ejemplo, el botón con el foco y Espacio) la use también.
    /// </summary>
    /// <param name="chord">La combinación pulsada.</param>
    /// <param name="isRepeat">La tecla sigue apretada (repetición automática de Windows).</param>
    /// <param name="focus">Dónde está el foco: un campo de texto o un menú recorrido con el teclado se quedan sus teclas.</param>
    public bool Dispatch(KeyChord chord, bool isRepeat, KeyFocus focus)
    {
        if (IsSuspended || Registry.FindAction(chord) is not { } actionId || Registry.Find(actionId) is not { } action)
        {
            return false;
        }

        if (focus == KeyFocus.TextInput && !action.WorksInTextInput)
        {
            return false;
        }

        // En un menú abierto con el teclado, Espacio, Intro, las flechas y las letras eligen; Ctrl+S y similares siguen siendo atajos.
        if (focus == KeyFocus.KeyboardMenu && (chord.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Windows)) == 0)
        {
            return false;
        }

        // Con una ficha o un marcador enfocados, Supr no borra los clips de la línea de tiempo ni Ctrl+A los selecciona.
        if (focus == KeyFocus.List && ListKeys.Contains(chord))
        {
            return false;
        }

        if (_restrictions.Exists(allowed => !allowed(action)))
        {
            return false;
        }

        if (isRepeat && !action.Repeats)
        {
            // Se consume para que la repetición no llegue a un control, pero no se lanza otra vez.
            return CanInvoke(actionId);
        }

        return Invoke(actionId);
    }

    /// <summary>Deja de lanzar acciones con el teclado hasta que se libera lo devuelto; se puede anidar.</summary>
    public IDisposable Suspend()
    {
        _suspended++;
        return new Resume(this);
    }

    /// <summary>
    /// Hasta que se libera lo devuelto, el teclado solo lanza las acciones que cumplen <paramref name="allowed"/> (por
    /// ejemplo, con una página encima del editor); los menús no cambian.
    /// </summary>
    public IDisposable Restrict(Func<ShortcutAction, bool> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        _restrictions.Add(allowed);
        return new Lift(this, allowed);
    }

    private Binding? FindHandler(string actionId)
    {
        if (!_bindings.TryGetValue(actionId, out List<Binding>? list))
        {
            return null;
        }

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].CanExecute())
            {
                return list[i];
            }
        }

        return null;
    }

    private sealed partial class Binding(ShortcutRouter router, string actionId, Action execute, Func<bool>? canExecute) : IDisposable
    {
        private bool _disposed;

        public void Execute() => execute();

        public bool CanExecute() => !_disposed && (canExecute?.Invoke() ?? true);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (router._bindings.TryGetValue(actionId, out List<Binding>? list))
            {
                list.Remove(this);
            }
        }
    }

    private sealed partial class Lift(ShortcutRouter router, Func<ShortcutAction, bool> allowed) : IDisposable
    {
        public void Dispose() => router._restrictions.Remove(allowed);
    }

    private sealed partial class Resume(ShortcutRouter router) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                router._suspended--;
            }
        }
    }
}
