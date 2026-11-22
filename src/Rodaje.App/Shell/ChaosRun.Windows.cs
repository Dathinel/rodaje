using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Diagnostics;
using Rodaje.App.Services;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Player;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Windows.Graphics;

namespace Rodaje.App.Shell;

/// <summary>
/// <c>--caos semilla minutos</c>: una persona errática con la app real y de fondo. Elige con <see cref="Random"/> de esa
/// semilla entre teclas y atajos (por <see cref="KeyboardShortcutHost.Press"/> y <see cref="ShortcutRouter"/>), los
/// controles a la vista y en los menús y diálogos abiertos (por sus <see cref="AutomationPeer"/>), arrastres de clips
/// (<see cref="ClipDragSession"/>, lo mismo que el ratón en la línea de tiempo), el cabezal, el tamaño de la ventana y el
/// corpus. Cada paso queda en <c>caos.log</c> con su número antes de ejecutarse, escrito hasta el disco, así un cierre
/// nativo no se lo lleva. Los diálogos de archivo responden con el corpus y con la carpeta de la corrida
/// (<see cref="AppDataPaths.Sandbox"/>). Sale con 0 si nada lanzó; un cuelgue de 10 s del hilo de la interfaz deja un
/// minivolcado en <c>fallos\</c> y termina el proceso.
/// </summary>
internal static partial class ChaosRun
{
    /// <summary>Lo que no se toca: sale de la carpeta de la corrida, abre ventanas a la vista o usa la red.</summary>
    private static readonly Regex Forbidden = new(
        @"sala|colabor|voz en off|grabar|micr[oó]fono|c[aá]mara|unirse|invita|anfitri|salir|otra pantalla|actualizaci|github|explorador|carpeta que lo contiene|sitio web|reportar|enviar",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ForbiddenActions = new(StringComparer.Ordinal)
    {
        PlayerShortcuts.SecondScreen, ShellShortcuts.CheckUpdates, ShellShortcuts.WatchedFolders,
    };

    private static readonly string[] Absurd =
        ["-1", "0", "NaN", "1e9", "-1e9", "abc", "", "∞", "1,5", "1.5", "99999999999999999999", "-0", "0x10", "  7 ", new string('9', 400), "1e-30", "%"];

    private static readonly int[] RandomKeys =
    [
        .. Enumerable.Range(0x41, 26), .. Enumerable.Range(0x30, 10), .. Enumerable.Range(0x70, 12),
        0x20, 0x0D, 0x1B, 0x2E, 0x08, 0x24, 0x23, 0x21, 0x22, 0x25, 0x26, 0x27, 0x28, 0x09, 0xBB, 0xBD, 0xBC, 0xBE,
    ];

    public static async Task RunAsync(MainWindow window, PlayerEngine engine, int seed, double minutes, IReadOnlyList<string> corpus)
    {
        string root = AppDataPaths.Sandbox ?? throw new InvalidOperationException("El modo caos exige su carpeta (AppDataPaths.Sandbox).");
        using var log = new ChaosLog(Path.Combine(root, "caos.log"));
        var chaos = new Chaos(window, new Random(seed), corpus, log, root);
        FileDialogs.FakeOpen = chaos.FakeOpen;
        FileDialogs.FakeSave = chaos.FakeSave;
        using var watchdog = new ChaosWatchdog(window.DispatcherQueue, log, Path.Combine(root, "fallos"));
        int failures = 0;
        log.Line(string.Create(CultureInfo.InvariantCulture, $"caos: semilla {seed}, {minutes} min, corpus de {corpus.Count} archivos"));
        var clock = Stopwatch.StartNew();
        int step = 0;
        while (clock.Elapsed.TotalMinutes < minutes && !chaos.Closed)
        {
            step++;
            watchdog.Step = step;
            try
            {
                await chaos.StepAsync(step);
            }
#pragma warning disable CA1031 // Lo que lanza una acción es lo que vería quien la hizo: se informa y se sigue.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures++;
                log.Line($"{step}\tEXCEPCIÓN\t{ex.GetType().Name}: {ex.Message}");
                Program.WriteCrash($"caos (semilla {seed}, paso {step}): {ex}");
            }

            if (engine.FailureMessage is { } failure && !chaos.EngineFailed)
            {
                chaos.EngineFailed = true;
                failures++;
                log.Line($"{step}\tMOTOR\t{failure}");
                Program.WriteCrash($"caos (semilla {seed}, paso {step}): el motor falló: {failure}");
            }
        }

        log.Line(string.Create(CultureInfo.InvariantCulture, $"caos: fin tras {step} pasos en {clock.Elapsed.TotalSeconds:0} s, {failures} fallos"));
        FileDialogs.FakeOpen = null;
        FileDialogs.FakeSave = null;
        Program.ExitCode = failures == 0 ? 0 : 2;
        window.Close();
    }

    /// <summary>Registro de pasos que sobrevive a un cierre nativo: cada línea llega al disco antes de seguir.</summary>
    private sealed partial class ChaosLog : IDisposable
    {
        private readonly FileStream _stream;
        private readonly object _gate = new();

        public ChaosLog(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }

        public void Line(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text.ReplaceLineEndings(" ") + "\n");
            lock (_gate)
            {
                _stream.Write(bytes);
                _stream.Flush(flushToDisk: true);
            }
        }

        public void Dispose() => _stream.Dispose();
    }

    /// <summary>Si el hilo de la interfaz no atiende en 10 s: lo anota, deja un minivolcado y termina el proceso.</summary>
    private sealed partial class ChaosWatchdog : IDisposable
    {
        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _queue;
        private readonly ChaosLog _log;
        private readonly string _folder;
        private readonly Thread _thread;
        private long _lastPong = Stopwatch.GetTimestamp();
        private volatile bool _disposed;

        public ChaosWatchdog(Microsoft.UI.Dispatching.DispatcherQueue queue, ChaosLog log, string folder)
        {
            _queue = queue;
            _log = log;
            _folder = folder;
            _thread = new Thread(Run) { IsBackground = true, Name = "Rodaje: vigilante del caos" };
            _thread.Start();
        }

        public int Step { get; set; }

        public void Dispose() => _disposed = true;

        private void Run()
        {
            while (!_disposed)
            {
                Thread.Sleep(500);
                _queue.TryEnqueue(() => Interlocked.Exchange(ref _lastPong, Stopwatch.GetTimestamp()));
                TimeSpan silent = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPong));
                if (silent > Limit && !_disposed)
                {
                    _log.Line(string.Create(CultureInfo.InvariantCulture, $"{Step}\tCUELGUE\tel hilo de la interfaz no responde desde hace {silent.TotalSeconds:0} s"));
                    Directory.CreateDirectory(_folder);
                    string dump = Path.Combine(_folder, $"cuelgue-paso-{Step}.dmp");
                    var writer = new Thread(() =>
                    {
                        using var self = Process.GetCurrentProcess();
                        _ = Services.Crashes.NativeCrashHandler.WriteMinidump(dump, self, out _);
                    }) { IsBackground = true };
                    writer.Start();
                    writer.Join(TimeSpan.FromSeconds(20));
                    using var process = Process.GetCurrentProcess();
                    process.Kill();
                    return;
                }
            }
        }
    }

    private sealed partial class Chaos(MainWindow window, Random random, IReadOnlyList<string> corpus, ChaosLog log, string root)
    {
        private readonly List<(int Weight, string Name, Func<Task> Act)> _actions = [];
        private int _step;
        private int _saved;

        public bool Closed { get; private set; }

        public bool EngineFailed { get; set; }

        private EditorSession Session => window.Session;

        public Task StepAsync(int step)
        {
            _step = step;
            if (_actions.Count == 0)
            {
                Build();
                window.Closed += (_, _) => Closed = true;
            }

            int total = _actions.Sum(a => a.Weight);
            int pick = random.Next(total);
            foreach ((int weight, string name, Func<Task> act) in _actions)
            {
                if ((pick -= weight) < 0)
                {
                    log.Line($"{step}\t{name}");
                    return RunAsync(act);
                }
            }

            return Task.CompletedTask;
        }

        private async Task RunAsync(Func<Task> act)
        {
            await act();
            // Como una persona rápida: casi siempre sin respiro, a veces deja que la interfaz y el visor se pongan al día.
            int pause = random.Next(10) switch { < 6 => random.Next(0, 30), < 9 => random.Next(30, 200), _ => random.Next(200, 1200) };
            await Task.Delay(pause);
        }

        private void Note(string detail) => log.Line($"{_step}\t  {detail}");

        private void Build()
        {
            Add(16, "tecla", PressKey);
            Add(14, "acción", InvokeAction);
            Add(26, "control", () => UseControlAsync());
            Add(4, "deshacer/rehacer en ráfaga", UndoBurst);
            Add(5, "cabezal en ráfaga", SeekBurst);
            Add(6, "reproducir/pausar", () => Invoke("playback.togglePlay"));
            Add(9, "arrastrar clip", DragClip);
            Add(5, "doble clic en un medio", AppendMedia);
            Add(3, "importar", () => Invoke("file.import"));
            Add(5, "seleccionar clips", SelectClips);
            Add(3, "copiar y pegar", CopyPaste);
            Add(4, "cerrar a medias", CloseSomething);
            Add(2, "tamaño de la ventana", ResizeWindow);
            Add(3, "pestaña del inspector", InspectorTab);
            Add(2, "esperar", () => Task.Delay(random.Next(500, 3000)));
        }

        private void Add(int weight, string name, Action act) => _actions.Add((weight, name, () => { act(); return Task.CompletedTask; }));

        private void Add(int weight, string name, Func<Task> act) => _actions.Add((weight, name, act));

        // ---- Teclado y atajos ----

        private void PressKey()
        {
            KeyChord chord;
            IReadOnlyList<ShortcutAction> actions = window.Router.Registry.Actions;
            if (random.Next(10) < 7 && actions.Count > 0)
            {
                ShortcutAction action = actions[random.Next(actions.Count)];
                IReadOnlyList<KeyChord> keys = window.Router.Registry.GetKeys(action.Id);
                if (keys.Count == 0 || ForbiddenActions.Contains(action.Id))
                {
                    Note($"{action.Id} no tiene tecla");
                    return;
                }

                chord = keys[random.Next(keys.Count)];
            }
            else
            {
                var modifiers = (KeyModifiers)random.Next(16) & (random.Next(3) == 0 ? (KeyModifiers)15 : KeyModifiers.None);
                chord = new KeyChord(RandomKeys[random.Next(RandomKeys.Length)], modifiers);
            }

            if (window.Router.Registry.FindAction(chord) is { } id && ForbiddenActions.Contains(id))
            {
                Note($"{chord.ToInvariantString()} ({id}) no se pulsa");
                return;
            }

            Note($"{chord.ToInvariantString()} → {window.Router.Registry.FindAction(chord) ?? "(sin acción)"} : {window.Keyboard.Press(chord)}");
        }

        private void InvokeAction()
        {
            IReadOnlyList<ShortcutAction> actions = window.Router.Registry.Actions;
            ShortcutAction action = actions[random.Next(actions.Count)];
            if (ForbiddenActions.Contains(action.Id))
            {
                Note($"{action.Id} no se usa");
                return;
            }

            Invoke(action.Id);
        }

        private void Invoke(string id) => Note($"{id}: {window.Router.Invoke(id)}");

        private void UndoBurst()
        {
            int count = random.Next(5, 51);
            bool keys = random.Next(2) == 0;
            Note($"{count} veces, {(keys ? "con teclas" : "con el menú")}");
            for (int i = 0; i < count; i++)
            {
                bool undo = random.Next(3) > 0;
                if (keys)
                {
                    window.Keyboard.Press(KeyChord.Parse(undo ? "Ctrl+Z" : "Ctrl+Y"));
                }
                else
                {
                    window.Router.Invoke(undo ? "edit.undo" : "edit.redo");
                }
            }
        }

        // ---- Línea de tiempo ----

        private async Task SeekBurst()
        {
            int count = random.Next(5, 60);
            double length = Math.Max(1, Session.Sequence.Duration.ToSeconds());
            Note(string.Create(CultureInfo.InvariantCulture, $"{count} saltos en {length:0.0} s"));
            for (int i = 0; i < count; i++)
            {
                double at = random.Next(8) switch { 0 => -random.NextDouble() * 10, 1 => length + random.NextDouble() * 100, _ => random.NextDouble() * length };
                Session.Seek(Time.FromSeconds(at));
                if (random.Next(4) == 0)
                {
                    await Task.Delay(random.Next(1, 20));
                }
            }
        }

        private List<(Track Track, Clip Clip)> AllClips() =>
            [.. Session.Sequence.AllTracks.SelectMany(t => t.Clips.Select(c => (t, c)))];

        private void SelectClips()
        {
            List<(Track Track, Clip Clip)> clips = AllClips();
            if (clips.Count == 0)
            {
                Note("no hay clips");
                return;
            }

            string[] ids = [.. clips.Where(_ => random.Next(3) == 0).Select(c => c.Clip.Id)];
            if (ids.Length == 0)
            {
                ids = [clips[random.Next(clips.Count)].Clip.Id];
            }

            Note($"{ids.Length} de {clips.Count}");
            Session.Select(ids);
        }

        private void DragClip()
        {
            List<(Track Track, Clip Clip)> clips = AllClips();
            if (clips.Count == 0)
            {
                Note("no hay clips");
                return;
            }

            (Track track, Clip clip) = clips[random.Next(clips.Count)];
            DragOperation[] operations = Enum.GetValues<DragOperation>();
            DragOperation operation = random.Next(3) == 0 ? operations[random.Next(operations.Length)] : (DragOperation)random.Next(3);
            Time grab = operation == DragOperation.TrimStart ? clip.Start : operation == DragOperation.TrimEnd ? clip.End : clip.Start + Time.FromTicks(clip.Duration.Ticks / 2);
            Track main = Session.MainTrack ?? track;
            var options = new DragOptions { MagneticTrackId = random.Next(4) == 0 ? null : main.Id, Snapping = random.Next(2) == 0, AutoRipple = random.Next(3) == 0, RippleScope = random.Next(3) == 0 ? RippleScope.AllTracks : RippleScope.Track, IgnoreGroups = random.Next(4) == 0, PixelsPerSecond = random.Next(1, 400) };
            var drag = new ClipDragSession(Session, new ClipDrag(Session.Project, Session.SequenceId, clip.Id, operation, grab, options, Session.Position));
            List<Track> same = [.. Session.Sequence.AllTracks.Where(t => t.Kind == track.Kind)];
            int moves = random.Next(1, 12);
            Note($"{operation} de {clip.Id} en {track.Id}, {moves} movimientos");
            drag.Begin();
            Time at = grab;
            for (int i = 0; i < moves; i++)
            {
                double delta = random.Next(10) switch { 0 => -1000, 1 => 1000, 2 => -random.NextDouble() * 60, _ => (random.NextDouble() - 0.5) * 10 };
                at = Time.FromSeconds(Math.Clamp(at.ToSeconds() + delta, -10000, 100000));
                TrackRow? row = random.Next(4) == 0 || same.Count == 0 ? null : RowOf(same[random.Next(same.Count)], main);
                drag.Move(at, row);
            }

            if (random.Next(8) == 0)
            {
                Note("cancelado");
                drag.Cancel();
            }
            else
            {
                drag.Complete();
            }
        }

        private TrackRow RowOf(Track track, Track main)
        {
            int index = Session.Sequence.VideoTracks.IndexOf(track);
            int row = index >= 0 ? Session.Sequence.VideoTracks.Count - 1 - index : Session.Sequence.VideoTracks.Count + Session.Sequence.AudioTracks.IndexOf(track);
            return new TrackRow(track, row, row * 48.0, 48, track == main);
        }

        private void AppendMedia()
        {
            List<MediaRef> media = Session.Project.Media;
            if (media.Count == 0)
            {
                Note("la biblioteca está vacía");
                return;
            }

            MediaRef pick = media[random.Next(media.Count)];
            Note($"{pick.Id} ({Path.GetFileName(pick.Path)}): {Session.AppendToTimeline(pick.Id)}");
        }

        private void CopyPaste()
        {
            string[] chords = ["Ctrl+C", "Ctrl+V", "Ctrl+Shift+V", "Ctrl+X", "Ctrl+D"];
            int count = random.Next(1, 6);
            var pressed = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string chord = chords[random.Next(chords.Length)];
                pressed.Add(chord);
                window.Keyboard.Press(KeyChord.Parse(chord));
            }

            Note(string.Join(' ', pressed));
        }

        private void InspectorTab()
        {
            InspectorView.Tab[] tabs = Enum.GetValues<InspectorView.Tab>();
            InspectorView.Tab tab = tabs[random.Next(tabs.Length)];
            Note(tab.ToString());
            window.InspectorPane.Show(tab);
        }

        // ---- Ventana ----

        private void ResizeWindow()
        {
            (int w, int h) = random.Next(6) switch
            {
                0 => (800, 600),
                1 => (640, 480),
                2 => (2560, 1440),
                3 => (1920, 1040),
                4 => (random.Next(300, 900), random.Next(200, 700)),
                _ => (1440, 900),
            };
            Note($"{w}×{h}");
            // De fondo la ventana sigue fuera de la pantalla: «maximizar» es el tamaño de un monitor grande, sin moverla.
            window.AppWindow.Resize(new SizeInt32(w, h));
        }

        private void CloseSomething()
        {
            XamlRoot? xamlRoot = window.Content?.XamlRoot;
            IReadOnlyList<Popup> popups = xamlRoot is null ? [] : VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot);
            if (popups.Count > 0 && random.Next(3) > 0)
            {
                Popup popup = popups[random.Next(popups.Count)];
                if (popup.Child is ContentDialog dialog)
                {
                    Note($"oculta el diálogo «{dialog.Title}»");
                    dialog.Hide();
                }
                else
                {
                    Note($"cierra un emergente ({popup.Child?.GetType().Name})");
                    popup.IsOpen = false;
                }

                return;
            }

            Note($"Esc: {window.Keyboard.Press(KeyChord.Parse("Esc"))}");
        }

        // ---- Controles ----

        private async Task UseControlAsync()
        {
            XamlRoot? xamlRoot = window.Content?.XamlRoot;
            if (xamlRoot is null)
            {
                return;
            }

            var found = new List<FrameworkElement>();
            IReadOnlyList<Popup> popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot);
            foreach (Popup popup in popups)
            {
                if (popup.Child is { } child)
                {
                    Collect(child, found);
                }
            }

            // Con un menú o un diálogo abierto, casi siempre se usa lo que hay en él.
            if (found.Count == 0 || random.Next(5) == 0)
            {
                Collect(window.Content!, found);
            }

            if (found.Count == 0)
            {
                Note("no hay controles");
                return;
            }

            FrameworkElement element = found[random.Next(found.Count)];
            string name = NameOf(element);
            if (Forbidden.IsMatch(name))
            {
                Note($"{element.GetType().Name} «{name}» no se toca");
                return;
            }

            await UseAsync(element, name);
        }

        private static void Collect(DependencyObject node, List<FrameworkElement> found)
        {
            if (node is UIElement { Visibility: not Visibility.Visible } or UIElement { IsHitTestVisible: false })
            {
                return;
            }

            if (node is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } element
                && element is ButtonBase or ComboBox or Slider or TextBox or NumberBox or ToggleSwitch or MenuFlyoutItemBase or MenuBarItem or SelectorItem or AutoSuggestBox
                && element is not Control { IsEnabled: false })
            {
                found.Add(element);
                if (element is ComboBox or NumberBox or AutoSuggestBox)
                {
                    return;
                }
            }

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                Collect(VisualTreeHelper.GetChild(node, i), found);
            }
        }

        private static string NameOf(FrameworkElement element)
        {
            string name = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(element);
            if (string.IsNullOrEmpty(name))
            {
                name = element switch
                {
                    ContentControl { Content: string text } => text,
                    MenuFlyoutItem item => item.Text,
                    MenuFlyoutSubItem sub => sub.Text,
                    MenuBarItem bar => bar.Title,
                    _ => FrameworkElementAutomationPeer.CreatePeerForElement(element)?.GetName() ?? "",
                };
            }

            return name + (string.IsNullOrEmpty(element.Name) ? "" : $" #{element.Name}");
        }

        private async Task UseAsync(FrameworkElement element, string name)
        {
            AutomationPeer? peer = FrameworkElementAutomationPeer.CreatePeerForElement(element);
            string what = $"{element.GetType().Name} «{name}»";
            switch (element)
            {
                case ComboBox combo:
                    await ComboAsync(combo, peer, what);
                    break;
                case Slider slider:
                    double value = random.Next(5) switch { 0 => slider.Minimum, 1 => slider.Maximum, _ => slider.Minimum + random.NextDouble() * (slider.Maximum - slider.Minimum) };
                    Note(string.Create(CultureInfo.InvariantCulture, $"{what} = {value:0.###}"));
                    slider.Value = value;
                    break;
                case NumberBox number:
                    await TypeAsync(number, what, text => number.Text = text, () => number.Value = random.Next(4) switch { 0 => double.NaN, 1 => 1e9, 2 => -1e9, _ => random.NextDouble() * 200 - 100 });
                    break;
                case TextBox box:
                    await TypeAsync(box, what, text => box.Text = text, null);
                    break;
                case AutoSuggestBox suggest:
                    await TypeAsync(suggest, what, text => suggest.Text = text, null);
                    break;
                case ToggleSwitch toggle:
                    Note(what);
                    toggle.IsOn = !toggle.IsOn;
                    break;
                case MenuBarItem or MenuFlyoutSubItem when peer?.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider expand:
                    Note($"abre {what}");
                    expand.Expand();
                    break;
                case SelectorItem when peer?.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider item:
                    Note($"elige {what}");
                    item.Select();
                    break;
                default:
                    if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggler && element is not Button)
                    {
                        Note($"alterna {what}");
                        toggler.Toggle();
                    }
                    else if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoker)
                    {
                        Note($"pulsa {what}");
                        invoker.Invoke();
                    }
                    else if (peer?.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider opener)
                    {
                        Note($"despliega {what}");
                        opener.Expand();
                    }
                    else
                    {
                        Note($"{what} no tiene acción");
                    }

                    break;
            }
        }

        private async Task ComboAsync(ComboBox combo, AutomationPeer? peer, string what)
        {
            int count = combo.Items.Count;
            var expand = peer?.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider;
            if (count == 0)
            {
                Note($"{what} vacío");
                expand?.Expand();
                return;
            }

            int index = random.Next(count);
            Note($"{what}: elige {index + 1} de {count} (estaba en {combo.SelectedIndex + 1})");
            expand?.Expand();
            await Task.Delay(random.Next(0, 80));
            if (combo.ContainerFromIndex(index) is SelectorItem container
                && FrameworkElementAutomationPeer.CreatePeerForElement(container)?.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider item)
            {
                item.Select();
            }
            else
            {
                combo.SelectedIndex = index;
            }

            if (random.Next(5) > 0)
            {
                combo.IsDropDownOpen = false;
            }
        }

        private async Task TypeAsync(Control box, string what, Action<string> setText, Action? setValue)
        {
            if (setValue is not null && random.Next(3) == 0)
            {
                Note($"{what}: valor absurdo");
                setValue();
                return;
            }

            string text = random.Next(3) == 0 ? random.Next(-1000, 1000).ToString(CultureInfo.InvariantCulture) : Absurd[random.Next(Absurd.Length)];
            Note($"{what} ← «{(text.Length > 20 ? text[..20] + "…" : text)}»");
            box.Focus(FocusState.Keyboard);
            setText(text);
            await Task.Delay(random.Next(0, 50));
            // Confirmar como quien sale del campo: el foco pasa al siguiente control.
            if (box.XamlRoot is { } xamlRoot)
            {
                _ = FocusManager.TryMoveFocus(FocusNavigationDirection.Next, new FindNextElementOptions { SearchRoot = xamlRoot.Content });
            }
        }

        // ---- Diálogos de archivo ----

        public IReadOnlyList<string> FakeOpen(string title, bool multiple, (string Name, string Spec)[] filters)
        {
            string[] patterns = [.. filters.SelectMany(f => f.Spec.Split(';')).Select(p => p.Trim().TrimStart('*').ToLowerInvariant()).Where(p => p.Length > 0 && p != ".*")];
            List<string> pool = [.. corpus.Concat(Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.rodaje", SearchOption.AllDirectories) : [])];
            List<string> matching = [.. pool.Where(p => patterns.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)))];
            IReadOnlyList<string> picked = random.Next(10) switch
            {
                0 => [],
                1 when pool.Count > 0 => [pool[random.Next(pool.Count)]],
                _ when matching.Count > 0 => multiple ? [.. matching.Where(_ => random.Next(3) == 0).DefaultIfEmpty(matching[random.Next(matching.Count)])] : [matching[random.Next(matching.Count)]],
                _ => [],
            };
            log.Line($"{_step}\t  diálogo «{title}» → {(picked.Count == 0 ? "cancelado" : string.Join(", ", picked.Select(Path.GetFileName)))}");
            return picked;
        }

        public string? FakeSave(string title, string suggestedName, string defaultExtension)
        {
            if (random.Next(10) == 0)
            {
                log.Line($"{_step}\t  diálogo «{title}» → cancelado");
                return null;
            }

            string folder = Path.Combine(root, "salidas");
            Directory.CreateDirectory(folder);
            string name = random.Next(6) == 0 && _saved > 0 ? $"guardado-{random.Next(_saved)}" : $"guardado-{_saved++}";
            string extension = defaultExtension.StartsWith('.') ? defaultExtension : "." + defaultExtension;
            string path = Path.Combine(folder, name + (string.IsNullOrWhiteSpace(defaultExtension) ? Path.GetExtension(suggestedName) : extension));
            log.Line($"{_step}\t  diálogo «{title}» → {Path.GetFileName(path)}");
            return path;
        }
    }
}
