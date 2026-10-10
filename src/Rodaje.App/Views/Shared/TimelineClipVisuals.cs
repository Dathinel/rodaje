using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services.Media;
using Rodaje.Core.Timing;
using Rodaje.Media.Video;
using MediaPeaks = Rodaje.App.Services.Media.WaveformPeaks;
using TimelinePeaks = Rodaje.App.Views.Timeline.WaveformPeaks;

namespace Rodaje.App.Views.Shared;

/// <summary>
/// Adaptador de <see cref="ClipVisuals"/> al contrato de la línea de tiempo (<see cref="Timeline.IClipVisuals"/>), que
/// pide las miniaturas de una en una y la forma de onda entera de cada medio. Solo desde el hilo de la interfaz.
/// </summary>
internal sealed partial class TimelineClipVisuals : Timeline.IClipVisuals
{
    /// <summary>Nivel de picos que recibe la línea de tiempo: 1024 muestras (21 ms a 48 kHz) por tramo.</summary>
    private const int PeakLevel = 1;

    /// <summary>Margen para pantallas con escala: la línea de tiempo pide el alto en píxeles independientes del DPI.</summary>
    private const double HeightScale = 1.5;

    private static readonly Time DefaultSpacing = Time.FromSeconds(1);

    private readonly ClipVisuals _visuals;
    private readonly Dictionary<string, (Time Last, Time Spacing)> _requests = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<MediaPeaks, TimelinePeaks> _peaks = new();

    public TimelineClipVisuals(ClipVisuals visuals)
    {
        ArgumentNullException.ThrowIfNull(visuals);
        _visuals = visuals;
        _visuals.Changed += (_, e) => Changed?.Invoke(this, e.MediaId);
    }

    public event EventHandler<string>? Changed;

    /// <remarks>
    /// La separación entre miniaturas se deduce de dos pedidos seguidos del mismo medio (la tira se recorre en orden) y
    /// decide la rejilla del instante, así un mismo zoom reutiliza siempre las mismas miniaturas.
    /// </remarks>
    public ImageSource? GetThumbnail(string mediaId, Time sourceTime, double height)
    {
        Time spacing = DefaultSpacing;
        if (_requests.TryGetValue(mediaId, out (Time Last, Time Spacing) previous))
        {
            long gap = Math.Abs(sourceTime.Ticks - previous.Last.Ticks);
            spacing = gap > 0 && gap < Time.TicksPerSecond * 3600 ? Time.FromTicks(gap) : previous.Spacing;
        }

        _requests[mediaId] = (sourceTime, spacing);
        int pixels = (int)Math.Ceiling(Math.Max(1, height) * HeightScale);
        BgraImage? image = _visuals.GetNearestThumbnail(mediaId, sourceTime, spacing, pixels);
        return image is null ? null : BgraBitmaps.From(image);
    }

    /// <summary>Los picos del medio cuando ya están completos; mientras se calculan, <c>null</c> (se avisa al terminar).</summary>
    public TimelinePeaks? GetWaveform(string mediaId)
    {
        if (_visuals.GetPeaks(mediaId) is not { IsComplete: true } peaks)
        {
            return null;
        }

        if (!_peaks.TryGetValue(peaks, out TimelinePeaks? converted))
        {
            Time bucket = Time.FromSamples(MediaPeaks.SamplesPerPeak(PeakLevel), peaks.SampleRate);
            converted = new TimelinePeaks(bucket, peaks.Envelope(PeakLevel));
            _peaks.AddOrUpdate(peaks, converted);
        }

        return converted;
    }
}
