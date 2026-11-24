using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Fase0.Uno;

public sealed record ClipRow(string Track, string Name, string Start);

/// <summary>Carga un <c>.rodaje</c> con Rodaje.Core, lo lista y cuenta clics; cada hito va al registro para la prueba de fuera.</summary>
public sealed partial class MainPage : Page, INotifyPropertyChanged
{
    private readonly Window _window;
    private readonly string? _log;
    private int _clicks;

    public MainPage(Window window)
    {
        _window = window;
        string[] args = Environment.GetCommandLineArgs();
        _log = Argument(args, "--registro");
        if (Argument(args, "--proyecto") is { } path)
        {
            Project project = ProjectSerializer.Load(path);
            Summary = $"{project.Name}: {project.Media.Count} medios, {project.Sequences.Count} secuencias";
            foreach (Sequence sequence in project.Sequences)
            {
                foreach (Track track in sequence.VideoTracks.Concat(sequence.AudioTracks))
                {
                    foreach (Clip clip in track.Clips)
                    {
                        Rows.Add(new ClipRow(track.Name, clip.Id, clip.Start.ToString() ?? ""));
                    }
                }
            }

            Note($"proyecto: {project.Name}; esquema {project.SchemaVersion}; clips {Rows.Count}");
        }

        InitializeComponent();
        Loaded += OnLoaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ClipRow> Rows { get; } = [];

    public string Summary { get; } = "Sin proyecto";

    public string ClickText => $"Clics: {_clicks}";

    private static string? Argument(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Centro de cada botón en píxeles de la ventana: quien prueba desde fuera hace clic ahí.
        double scale = XamlRoot?.RasterizationScale ?? 1;
        foreach ((string name, Button button) in new[] { ("boton_contar", CountButton), ("boton_cerrar", CloseButton) })
        {
            var center = button.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(button.ActualWidth / 2, button.ActualHeight / 2));
            Note(string.Create(CultureInfo.InvariantCulture, $"{name}: {center.X * scale:0} {center.Y * scale:0}"));
        }

        using Process self = Process.GetCurrentProcess();
        Note(string.Create(CultureInfo.InvariantCulture, $"ventana_lista_ms: {(DateTime.Now - self.StartTime).TotalMilliseconds:0}"));
    }

    private void OnCount(object sender, RoutedEventArgs e)
    {
        _clicks++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ClickText)));
        Note($"clic: {_clicks}");
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Note("cerrar");
        _window.Close();
    }

    private void Note(string line)
    {
        Console.WriteLine(line);
        if (_log is not null)
        {
            File.AppendAllText(_log, line + "\n");
        }
    }
}
