using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Theme;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Rodaje.App.Views.Shortcuts;

/// <summary>Resultado de guardar la hoja rápida: la ruta, o el error en español.</summary>
internal sealed record CheatSheetSaved(string? Path, string? Error);

/// <summary>La tarjeta de la hoja rápida y su exportación a Documentos.</summary>
internal sealed partial class CheatSheetView : UserControl
{
    private const int Columns = 3;
    private const double PngScale = 2;
    private const string FileStem = "Atajos de Rodaje";

    private IReadOnlyList<CheatSheetEntry> _entries = [];

    public CheatSheetView()
    {
        InitializeComponent();
    }

    public event EventHandler<CheatSheetSaved>? Saved;

    public void Show(IReadOnlyList<CheatSheetEntry> entries, string title, string subtitle)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries;
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        Entries.Children.Clear();
        Entries.ColumnDefinitions.Clear();
        Entries.RowDefinitions.Clear();
        int rows = (entries.Count + Columns - 1) / Columns;
        for (int c = 0; c < Columns; c++)
        {
            Entries.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        }

        for (int r = 0; r < rows; r++)
        {
            Entries.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        }

        for (int i = 0; i < entries.Count; i++)
        {
            CheatSheetEntry entry = entries[i];
            var cell = new Grid { ColumnSpacing = 8, BorderBrush = GroupBrushes.Resource("StrokeBrush"), BorderThickness = new Thickness(0, 0, 0, 1) };
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cell.Children.Add(new TextBlock { Text = entry.Label, Style = (Style)Application.Current.Resources["BodyTextStyle"] });
            var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            foreach (string key in entry.Keys)
            {
                keys.Children.Add(new Border
                {
                    Background = GroupBrushes.Resource("ControlBrush"),
                    BorderBrush = GroupBrushes.For(entry.Group),
                    BorderThickness = new Thickness(1, 1, 1, 2),
                    CornerRadius = ThemeResources.Corner("ControlCornerRadius"),
                    Padding = new Thickness(6, 1, 6, 1),
                    Child = new TextBlock { Text = key, FontSize = ThemeResources.Size("CaptionFontSize"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = GroupBrushes.Resource("TextBrush") },
                });
            }

            Grid.SetColumn(keys, 1);
            cell.Children.Add(keys);
            AutomationProperties.SetName(cell, $"{entry.Label}: {string.Join(" o ", entry.Keys)}");
            Grid.SetRow(cell, i % rows);
            Grid.SetColumn(cell, i / rows);
            Entries.Children.Add(cell);
        }
    }

    private static string DocumentsPath(string extension)
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(folder))
        {
            folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return System.IO.Path.Combine(folder, FileStem + extension);
    }

    private async void OnSaveHtmlClick(object sender, RoutedEventArgs e)
    {
        string path = DocumentsPath(".html");
        try
        {
            string html = CheatSheet.ToHtml(_entries, TitleText.Text, SubtitleText.Text);
            await File.WriteAllTextAsync(path, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Saved?.Invoke(this, new CheatSheetSaved(path, null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Saved?.Invoke(this, new CheatSheetSaved(null, $"No se pudo guardar la hoja: {ex.Message}"));
        }
    }

    private async void OnSavePngClick(object sender, RoutedEventArgs e)
    {
        string path = DocumentsPath(".png");
        try
        {
            // Al doble de la pantalla, para que se lea bien impresa o de fondo de escritorio.
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(Card, (int)Math.Ceiling(Card.ActualWidth * PngScale), 0);
            byte[] pixels = (await bitmap.GetPixelsAsync()).ToArray();
            double dpi = 96 * bitmap.PixelWidth / Math.Max(1, Card.ActualWidth);
            using var stream = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, pixels);
            await encoder.FlushAsync();
            stream.Seek(0);
            await using (FileStream file = File.Create(path))
            {
                await stream.AsStreamForRead().CopyToAsync(file);
            }

            Saved?.Invoke(this, new CheatSheetSaved(path, null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            Saved?.Invoke(this, new CheatSheetSaved(null, $"No se pudo guardar la imagen: {ex.Message}"));
        }
    }
}
