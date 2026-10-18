using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Packages;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Presentation;
using Rodaje.Packages;

namespace Rodaje.App.Views.Library;

/// <summary>
/// Un medio de la biblioteca tal como se muestra: portada (y la imagen del recorrido al pasar el ratón), duración,
/// estado del proxy, valoración, marca, etiquetas, metadatos y avisos (sin conexión, repetido). Un subclip es una ficha
/// más del mismo medio con su tramo.
/// </summary>
internal sealed partial class MediaItem(MediaRef media, bool isUsed, Subclip? subclip = null) : ObservableObject
{
    private readonly MediaRef _source = media.Clone();
    private ImageSource? _thumbnail;
    private ImageSource? _skim;
    private int _rating;
    private MediaMark _mark;
    private string _tagsText = "";
    private bool _offline;
    private string _duplicateOf = "";
    private string _badge = "";
    private bool _isFavorite;
    private string _codecs = MediaDetails.Pending;
    private bool _codecsKnown;

    public string Id { get; } = media.Id;

    /// <summary>El subclip que muestra la ficha, o <c>null</c> si es el medio entero.</summary>
    public Subclip? Subclip { get; } = subclip;

    public bool IsSubclip => Subclip is not null;

    /// <summary>Lo que se arrastra a la línea de tiempo: el id, y el tramo si es un subclip (<see cref="Timeline.MediaDrop"/>).</summary>
    public string DragText => Timeline.MediaDrop.Format(Id, Subclip is { } s ? (s.In, s.Out) : null);

    public string Name { get; } = subclip?.Name ?? media.Name;

    /// <summary>Duración del medio (o del subclip), para el recorrido con el ratón.</summary>
    public Time Duration { get; } = subclip?.Duration ?? media.Duration;

    /// <summary>Dónde empieza lo que muestra la ficha en el medio (0, o la entrada del subclip).</summary>
    public Time Offset { get; } = subclip?.In ?? Time.Zero;

    /// <summary>Ruta absoluta del archivo (en memoria las rutas del proyecto son absolutas).</summary>
    public string Path { get; } = media.Path;

    public bool HasVideo { get; } = media.HasVideo;

    /// <summary>Algún clip lo usa (no se puede quitar del proyecto).</summary>
    public bool IsUsed { get; set; } = isUsed;

    public string DurationText { get; } = MediaDetails.DurationText(media, subclip);

    public string Detail { get; } = media.HasVideo
        ? string.Create(CultureInfo.InvariantCulture, $"{media.Width}×{media.Height}")
        : media.AudioChannels switch { 1 => "mono", 2 => "estéreo", > 2 => $"{media.AudioChannels} canales", _ => "audio" };

    public string Tooltip
    {
        get
        {
            var lines = new List<string> { Name, Path, MediaDetails.Summary(_source, DurationText, _codecs) };
            if (Camera is { Length: > 0 } || Created is not null)
            {
                lines.Add(string.Join(" · ", new[] { DateText, Camera ?? "" }.Where(t => t.Length > 0 && t != "—")));
            }

            if (_tagsText.Length > 0)
            {
                lines.Add("Etiquetas: " + _tagsText);
            }

            if (_offline)
            {
                lines.Add("Sin conexión: el archivo no está donde dice el proyecto. Menú › Volver a enlazar…");
            }

            if (_duplicateOf.Length > 0)
            {
                lines.Add("Repetido: " + _duplicateOf);
            }

            if (IsSubclip)
            {
                lines.Add($"Subclip de «{_source.Name}» desde {Timecode.Format(Offset)}");
            }

            return string.Join("\n", lines);
        }
    }

    /// <summary>Fecha de grabación (o nula mientras no se lea, o si el archivo no la trae).</summary>
    public DateTimeOffset? Created { get; private set; }

    public string? Camera { get; private set; }

    /// <summary>Columna de datos: «2026-10-03» o «—».</summary>
    public string DateText => Created is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "—";

    public string CameraText => Camera ?? "—";

    /// <summary>Estrellas de la valoración («★★★☆☆»); vacío sin valorar.</summary>
    public string RatingText => _rating == 0 ? "" : new string('★', _rating) + new string('☆', LibraryOrganization.MaxRating - _rating);

    public Visibility RatingVisibility => _rating == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Un rechazado se ve atenuado.</summary>
    public double TileOpacity => _mark == MediaMark.Rejected ? 0.4 : 1;

    public Visibility RejectedVisibility => _mark == MediaMark.Rejected ? Visibility.Visible : Visibility.Collapsed;

    public string TagsText => _tagsText;

    public Visibility TagsVisibility => _tagsText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public bool IsOffline => _offline;

    public Visibility OfflineVisibility => _offline ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DuplicateVisibility => _duplicateOf.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility SubclipVisibility => IsSubclip ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Lo que se ve en la portada: la imagen del recorrido mientras el ratón pasa, si no la portada.</summary>
    public ImageSource? Displayed => _skim ?? _thumbnail;

    /// <summary>Pone (o quita, con <c>null</c>) la imagen del recorrido con el ratón.</summary>
    public void Skim(ImageSource? image)
    {
        if (!ReferenceEquals(_skim, image))
        {
            _skim = image;
            OnPropertyChanged(nameof(Displayed));
        }
    }

    /// <summary>Lo que dice la organización y el disco del medio: valoración, marca, etiquetas, sin conexión y repetido.</summary>
    public void SetMarks(int rating, MediaMark mark, IReadOnlyList<string> tags, bool offline, string duplicateOf)
    {
        string tagsText = string.Join(", ", tags);
        if (_rating == rating && _mark == mark && _tagsText == tagsText && _offline == offline && _duplicateOf == duplicateOf)
        {
            return;
        }

        (_rating, _mark, _tagsText, _offline, _duplicateOf) = (rating, mark, tagsText, offline, duplicateOf);
        OnPropertyChanged(nameof(RatingText));
        OnPropertyChanged(nameof(RatingVisibility));
        OnPropertyChanged(nameof(TileOpacity));
        OnPropertyChanged(nameof(RejectedVisibility));
        OnPropertyChanged(nameof(TagsText));
        OnPropertyChanged(nameof(TagsVisibility));
        OnPropertyChanged(nameof(OfflineVisibility));
        OnPropertyChanged(nameof(DuplicateVisibility));
        OnPropertyChanged(nameof(Tooltip));
    }

    /// <summary>Columna de datos: «1920×1080» o «—» si es solo audio.</summary>
    public string ResolutionText { get; } = MediaDetails.Resolution(media);

    /// <summary>Columna de datos: «29,97 fps» o «—» en una foto o un audio.</summary>
    public string FrameRateText { get; } = MediaDetails.FrameRate(media);

    /// <summary>Columna de datos: «H.264 · AAC»; se lee del archivo la primera vez que se muestra la lista.</summary>
    public string Codecs => _codecs;

    public bool CodecsKnown => _codecsKnown;

    /// <summary>No se pudo leer el códec; se reintenta en la próxima lectura (solo abre el archivo si cambió).</summary>
    public bool CodecsFailed { get; private set; }

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
    public Time CoverTime { get; } = (subclip?.In ?? Time.Zero) + Time.FromSeconds(Math.Min(1.0, (subclip?.Duration ?? media.Duration).ToSeconds() * 0.2));

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
                OnPropertyChanged(nameof(Displayed));
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
    public void SetCodecs(string? video, string? audio) => SetCodecs(new MediaCodecs(video, audio, Failed: false));

    /// <summary>Pone lo leído del archivo; si falló, la columna queda vacía y <see cref="CodecsFailed"/> lo recuerda.</summary>
    public void SetCodecs(MediaCodecs codecs)
    {
        (string? video, string? audio) = (codecs.Video, codecs.Audio);
        CodecsFailed = codecs.Failed;
        _codecsKnown = true;
        _codecs = MediaDetails.Codecs(video, audio, known: true);
        Created = codecs.Created;
        Camera = codecs.Camera;
        OnPropertyChanged(nameof(DateText));
        OnPropertyChanged(nameof(CameraText));
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
            CodecsFailed = previous.CodecsFailed;
            Created = previous.Created;
            Camera = previous.Camera;
        }
    }

    /// <summary>La ficha sigue valiendo para el medio: nada de lo que muestra cambió.</summary>
    public bool Shows(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return Subclip is null && media.Id == _source.Id && media.Name == _source.Name && media.Path == _source.Path && media.Duration == _source.Duration
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
internal sealed partial class ElementItem(LibraryElement element) : ObservableObject
{
    private ImageSource? _preview;

    public LibraryElement Element { get; } = element;

    /// <summary>Miniatura (animada al pasar el puntero) de una transición; nula en lo demás.</summary>
    public ImageSource? Preview
    {
        get => _preview;
        set
        {
            if (SetProperty(ref _preview, value))
            {
                OnPropertyChanged(nameof(GlyphVisibility));
                OnPropertyChanged(nameof(PreviewShadeVisibility));
            }
        }
    }

    public Visibility GlyphVisibility => _preview is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PreviewShadeVisibility => _preview is null ? Visibility.Collapsed : Visibility.Visible;

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
