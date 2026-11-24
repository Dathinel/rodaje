using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Shell;
using Rodaje.App.Views.Player;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Packages;
using Rodaje.Packages.Content;
using Rodaje.Playback.Composition;
using Rodaje.Render;

namespace Rodaje.App.Views.Inspector.Filters;

/// <summary>Un preset de filtro en la galería: su nombre, de dónde viene y su miniatura sobre el cuadro actual.</summary>
internal sealed partial class PresetTile(string name, string source, string reference, FilterPreset preset) : ObservableObject
{
    private WriteableBitmap? _thumbnail;

    public string Name { get; } = name;

    public string Source { get; } = source;

    public string Reference { get; } = reference;

    public FilterPreset Preset { get; } = preset;

    public string Tip => $"{Name} · {Source}. Clic: aplicar al clip (reemplaza el mismo preset si ya estaba).";

    public WriteableBitmap? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }
}

/// <summary>
/// Los presets de filtro (de los paquetes y los propios) con una miniatura del cuadro actual compuesta en la GPU con cada
/// uno, y «Guardar como preset»: los filtros de imagen del clip a un <c>rodaje.filter</c> en los complementos propios.
/// </summary>
internal sealed partial class FiltersPanel
{
    /// <summary>Ancho de las miniaturas, en píxeles.</summary>
    private const int ThumbnailWidth = 192;

    private readonly ObservableCollection<PresetTile> _presets = [];
    private int _presetVersion;
    private bool _presetRefreshQueued;

    public ObservableCollection<PresetTile> Presets => _presets;

    /// <summary>Rehace la galería y sus miniaturas en cuanto se pueda (una sola vez aunque se pida varias).</summary>
    private void QueuePresetRefresh()
    {
        if (_presetRefreshQueued || _services is null)
        {
            return;
        }

        _presetRefreshQueued = true;
        DispatcherQueue.RunLater(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _presetRefreshQueued = false;
            UiTask.Run(RefreshPresetsAsync());
        });
    }

    private void OnRefreshPresetsClick(object sender, RoutedEventArgs e) => QueuePresetRefresh();

    private async Task RefreshPresetsAsync()
    {
        if (_services is not { } services || Visibility != Visibility.Visible)
        {
            return;
        }

        int version = ++_presetVersion;
        PackageScan installed = services.Packages.Installed;
        var loader = new InstalledContentLoader(() => installed);
        List<(PackageItemEntry Entry, FilterPreset Preset)> found = await Task.Run(() =>
        {
            var list = new List<(PackageItemEntry, FilterPreset)>();
            foreach (PackageItemEntry entry in installed.Items(ItemKind.Filter))
            {
                try
                {
                    if (loader.LoadFilter(entry.Reference.ToString()) is { } preset)
                    {
                        list.Add((entry, preset));
                    }
                }
                catch (Exception ex) when (ex is PackageContentException or IOException or UnauthorizedAccessException)
                {
                }
            }

            return list;
        });
        if (version != _presetVersion)
        {
            return;
        }

        _presets.Clear();
        foreach ((PackageItemEntry entry, FilterPreset preset) in found)
        {
            string source = entry.Package.Origin == PackageOrigin.Own ? "Mío" : entry.Package.Manifest.Name;
            _presets.Add(new PresetTile(entry.Item.Name, source, entry.Reference.ToString(), preset));
        }

        NoPresetsText.Visibility = _presets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PresetsSection.Summary = _presets.Count switch
        {
            0 => "ninguno",
            1 => "1 preset",
            int n => $"{n} presets",
        };
        await RenderThumbnailsAsync(services.Player, version);
    }

    /// <summary>El cuadro del cursor sin color (lo que cada preset reemplaza), reducido y compuesto con cada preset.</summary>
    private async Task RenderThumbnailsAsync(PlayerEngine engine, int version)
    {
        if (_presets.Count == 0)
        {
            return;
        }

        FrameCapture frame;
        try
        {
            frame = await engine.CaptureFrameAsync(PreviewBypass.Grading).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is TimeoutException or TaskCanceledException or InvalidOperationException)
        {
            return;
        }

        FrameCapture small = Reduce(frame, ThumbnailWidth);
        IEffectResources? content = _services?.Content;
        List<PresetTile> tiles = [.. _presets];
        IReadOnlyList<IReadOnlyList<LayerEffect>> looks = [.. tiles.Select(t => (IReadOnlyList<LayerEffect>)EffectEvaluation.Evaluate(
            FilterPresetFile.ToInstances(t.Preset), Time.Zero, content))];
        IReadOnlyList<byte[]> images;
        try
        {
            images = await engine.RenderLooksAsync(small, looks).WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            return;
        }

        if (version != _presetVersion)
        {
            return;
        }

        for (int i = 0; i < tiles.Count && i < images.Count; i++)
        {
            var bitmap = new WriteableBitmap(small.Width, small.Height);
            ToBgra(images[i]).CopyTo(bitmap.PixelBuffer);
            bitmap.Invalidate();
            tiles[i].Thumbnail = bitmap;
        }
    }

    /// <summary>Reduce con promedio de cajas a <paramref name="width"/> de ancho (alto par, para las texturas).</summary>
    internal static FrameCapture Reduce(FrameCapture frame, int width)
    {
        width = Math.Max(2, Math.Min(width, frame.Width));
        int height = Math.Max(2, (int)Math.Round((double)frame.Height * width / frame.Width / 2) * 2);
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int y0 = y * frame.Height / height, y1 = Math.Max(y0 + 1, (y + 1) * frame.Height / height);
            for (int x = 0; x < width; x++)
            {
                int x0 = x * frame.Width / width, x1 = Math.Max(x0 + 1, (x + 1) * frame.Width / width);
                int r = 0, g = 0, b = 0, n = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int i = ((sy * frame.Width) + sx) * 4;
                        r += frame.Rgba[i];
                        g += frame.Rgba[i + 1];
                        b += frame.Rgba[i + 2];
                        n++;
                    }
                }

                int o = ((y * width) + x) * 4;
                pixels[o] = (byte)(r / n);
                pixels[o + 1] = (byte)(g / n);
                pixels[o + 2] = (byte)(b / n);
                pixels[o + 3] = 255;
            }
        }

        return new FrameCapture(pixels, width, height);
    }

    private static byte[] ToBgra(byte[] rgba)
    {
        var bgra = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = 255;
        }

        return bgra;
    }

    private void OnPresetClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PresetTile tile || _services is null || _clipId is null)
        {
            return;
        }

        string sequenceId = _services.Session.SequenceId;
        if (InspectorTargets.AsOneStep(Targets().ConvertAll(t => EffectCommands.ApplyFilter(sequenceId, t.Clip.Id, tile.Preset, tile.Reference))) is { } step)
        {
            _services.Session.Execute(step);
        }
    }

    private void OnSavePresetClick(object sender, RoutedEventArgs e) => UiTask.Run(OnSavePresetClickAsync(sender, e));

    private async Task OnSavePresetClickAsync(object sender, RoutedEventArgs e)
    {
        if (DialogGate.IsOpen || _services is not { } services || CurrentClip() is not { } clip)
        {
            return;
        }

        if (!clip.Effects.Exists(FilterPresetFile.Belongs))
        {
            services.Session.Notify(NotificationSeverity.Informational, "Guardar como preset",
                "El clip no tiene filtros de imagen activos (color, curvas, desenfoque, grano…).");
            return;
        }

        const string DefaultName = "Mi preset";
        var nameBox = new TextBox { Text = DefaultName, SelectionStart = 0, SelectionLength = DefaultName.Length };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "Los filtros de imagen del clip, con sus valores en el cursor, quedarán en «Mis filtros»: listos para otros clips o para publicarlos en un paquete.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(nameBox);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Guardar como preset",
            Content = content,
            PrimaryButtonText = "Guardar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary || CurrentClip() is not { } current)
        {
            return;
        }

        string name = string.IsNullOrWhiteSpace(nameBox.Text) ? DefaultName : nameBox.Text.Trim();
        try
        {
            Directory.CreateDirectory(AnimationPreset.UserFolder);
            Time at = current.ToEffectTime(services.Session.Position);
            string path = FilterPresetFile.Save(AnimationPreset.UserFolder, name, current.Effects, at);
            services.Session.Notify(NotificationSeverity.Success, "Preset guardado", $"«{name}» quedó en tus complementos propios: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            services.Session.Notify(NotificationSeverity.Error, "No se pudo guardar el preset", ex.Message);
        }
    }
}
