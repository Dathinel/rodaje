using System.Globalization;
using Rodaje.App.Services.Format;
using Rodaje.Core.Model;
using Rodaje.Engine.Presentation;

namespace Rodaje.App.Services.Inspector;

/// <summary>Lo que muestra el panel Propiedades sin nada seleccionado: el formato y el contenido de la secuencia abierta.</summary>
public static class SequenceSummary
{
    public static IReadOnlyList<(string Label, string Value)> Describe(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        int clips = sequence.AllTracks.Sum(t => t.Clips.Count);
        return
        [
            ("Formato", ProjectFormats.AspectName(sequence.Width, sequence.Height)),
            ("Resolución", string.Create(CultureInfo.InvariantCulture, $"{sequence.Width}×{sequence.Height}")),
            ("Cuadros por segundo", ProjectFormats.Fps(sequence.FrameRate)),
            ("Duración", Timecode.Format(sequence.Duration)),
            ("Clips", clips.ToString(CultureInfo.InvariantCulture)),
            ("Pistas", string.Create(CultureInfo.InvariantCulture, $"{sequence.VideoTracks.Count} de video y {sequence.AudioTracks.Count} de audio")),
        ];
    }
}
