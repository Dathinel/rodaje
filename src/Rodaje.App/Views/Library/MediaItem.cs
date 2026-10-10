using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Engine.Presentation;

namespace Rodaje.App.Views.Library;

/// <summary>Un medio de la biblioteca tal como se muestra.</summary>
internal sealed partial class MediaItem(MediaRef media)
{
    public string Id { get; } = media.Id;

    public string Name { get; } = media.Name;

    public string Detail { get; } = Describe(media);

    /// <summary>Glifo de Segoe Fluent Icons: video o audio.</summary>
    public string Glyph { get; } = media.HasVideo ? "" : "";

    private static string Describe(MediaRef media)
    {
        string duration = Timecode.Format(media.Duration);
        return media.HasVideo
            ? string.Create(CultureInfo.InvariantCulture, $"{duration} · {media.Width}×{media.Height}")
            : duration;
    }
}
