using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Packages;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Presentation;
using Rodaje.Paquetes;

namespace Rodaje.App.Views.Library;

/// <summary>Un medio de la biblioteca tal como se muestra: portada, duración y estado del proxy.</summary>
internal sealed partial class MediaItem(MediaRef media, bool isUsed) : ObservableObject
{
    private ImageSource? _thumbnail;
    private string _badge = "";

    public string Id { get; } = media.Id;

    public string Name { get; } = media.Name;

    /// <summary>Ruta absoluta del archivo (en memoria las rutas del proyecto son absolutas).</summary>
    public string Path { get; } = media.Path;

    public bool HasVideo { get; } = media.HasVideo;

    /// <summary>Algún clip lo usa (no se puede quitar del proyecto).</summary>
    public bool IsUsed { get; set; } = isUsed;

    public string DurationText { get; } = Timecode.Format(media.Duration);

    public string Detail { get; } = media.HasVideo
        ? string.Create(CultureInfo.InvariantCulture, $"{media.Width}×{media.Height}")
        : media.AudioChannels switch { 1 => "mono", 2 => "estéreo", > 2 => $"{media.AudioChannels} canales", _ => "audio" };

    public string Tooltip { get; } = $"{media.Name}\n{media.Path}";

    /// <summary>Instante de la portada: un poco después del inicio, donde casi nunca hay negro.</summary>
    public Time CoverTime { get; } = Time.FromSeconds(Math.Min(1.0, media.Duration.ToSeconds() * 0.2));

    /// <summary>Glifo de Segoe Fluent Icons: video o nota musical.</summary>
    public string Glyph { get; } = media.HasVideo ? "\uE714" : "\uE8D6";

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (SetProperty(ref _thumbnail, value))
            {
                OnPropertyChanged(nameof(GlyphVisibility));
            }
        }
    }

    public Visibility GlyphVisibility => _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Texto corto sobre la portada (por ejemplo, el avance del proxy).</summary>
    public string Badge
    {
        get => _badge;
        set
        {
            if (SetProperty(ref _badge, value))
            {
                OnPropertyChanged(nameof(BadgeVisibility));
            }
        }
    }

    public Visibility BadgeVisibility => _badge.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>Un elemento de texto, efecto, transición, filtro o sonido.</summary>
internal sealed partial class ElementItem(LibraryElement element)
{
    public LibraryElement Element { get; } = element;

    public string Name { get; } = element.Name;

    public string Source { get; } = element.Source;

    public string Tooltip { get; } = (string.IsNullOrEmpty(element.Description) ? element.Name : $"{element.Name}\n{element.Description}")
        + (ElementApplier.CanApply(element) ? "\nDoble clic: aplicar al clip seleccionado." : "");

    public double Opacity { get; } = element.IsAvailable ? 1 : 0.5;

    public string Glyph { get; } = element.Origin switch
    {
        PackageOrigin.Own => "\uE77B",
        null => "\uE7C9",
        _ => "\uE7B8",
    };
}

/// <summary>Un módulo de IA con su estado de instalación.</summary>
internal sealed partial class ModuleItem(PluginEntry entry)
{
    public string Name { get; } = entry.Name;

    public string Description { get; } = entry.Description;

    public string State { get; } = entry.IsInstalled ? $"Instalado · {entry.InstalledVersion}" : "No instalado";

    public double StateOpacity { get; } = entry.IsInstalled ? 1 : 0.6;
}
