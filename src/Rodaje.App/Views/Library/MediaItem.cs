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
    private readonly MediaRef _source = media.Clone();
    private ImageSource? _thumbnail;
    private string _badge = "";
    private bool _isFavorite;
    private string _codecs = MediaDetails.Pending;
    private bool _codecsKnown;

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

    public string Tooltip => $"{Name}\n{Path}\n{MediaDetails.Summary(_source, DurationText, _codecs)}";

    /// <summary>Columna de datos: «1920×1080» o «—» si es solo audio.</summary>
    public string ResolutionText { get; } = MediaDetails.Resolution(media);

    /// <summary>Columna de datos: «29,97 fps» o «—» en una foto o un audio.</summary>
    public string FrameRateText { get; } = MediaDetails.FrameRate(media);

    /// <summary>Columna de datos: «H.264 · AAC»; se lee del archivo la primera vez que se muestra la lista.</summary>
    public string Codecs => _codecs;

    public bool CodecsKnown => _codecsKnown;

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value))
            {
                OnPropertyChanged(nameof(FavoriteVisibility));
            }
        }
    }

    public Visibility FavoriteVisibility => _isFavorite ? Visibility.Visible : Visibility.Collapsed;

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

    /// <summary>Pone los códecs leídos del archivo (vacíos si no se pudo leer).</summary>
    public void SetCodecs(string? video, string? audio)
    {
        _codecsKnown = true;
        _codecs = MediaDetails.Codecs(video, audio, known: true);
        OnPropertyChanged(nameof(Codecs));
        OnPropertyChanged(nameof(Tooltip));
    }

    /// <summary>Hereda lo ya calculado de la ficha anterior del mismo archivo (portada, proxy y códecs).</summary>
    public void CopyFrom(MediaItem previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        Thumbnail = previous.Thumbnail;
        Badge = previous.Badge;
        if (previous._codecsKnown)
        {
            _codecsKnown = true;
            _codecs = previous._codecs;
        }
    }

    /// <summary>La ficha sigue valiendo para el medio: nada de lo que muestra cambió.</summary>
    public bool Shows(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return media.Id == _source.Id && media.Name == _source.Name && media.Path == _source.Path && media.Duration == _source.Duration
            && media.HasVideo == _source.HasVideo && media.HasAudio == _source.HasAudio && media.FrameRate == _source.FrameRate
            && media.Width == _source.Width && media.Height == _source.Height && media.SampleRate == _source.SampleRate
            && media.AudioChannels == _source.AudioChannels;
    }
}

/// <summary>Una carpeta de la biblioteca en su ficha: nombre y cuánto tiene.</summary>
internal sealed partial class FolderItem(LibraryFolder folder, string summary)
{
    public string Id { get; } = folder.Id;

    public string Name { get; } = folder.Name;

    public string Summary { get; } = summary;

    public string Tooltip { get; } = $"{folder.Name}\n{summary}\nClic: abrir. Suelta medios aquí para moverlos.";
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
