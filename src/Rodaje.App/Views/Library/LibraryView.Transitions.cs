using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Packages;
using Rodaje.App.Views.Inspector.Transition;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Packages;

namespace Rodaje.App.Views.Library;

/// <summary>
/// Miniaturas de la galería de transiciones: cada una compuesta en la GPU entre dos imágenes de muestra (la misma caché que
/// el inspector), quieta en la mitad y animada mientras el puntero está encima.
/// </summary>
internal sealed partial class LibraryView
{
    private const int ThumbWidth = 128;
    private const int ThumbHeight = 72;
    private const double ThumbHoldStart = 0.25;
    private const double ThumbHoldEnd = 0.4;
    private static readonly Time GalleryDuration = Time.FromSeconds(0.8);
    private readonly Dictionary<ElementItem, (TransitionAnimation Animation, WriteableBitmap Bitmap)> _transitionThumbs = [];
    private readonly DispatcherTimer _thumbClock = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private ElementItem? _hovered;
    private double _hoverTime;
    private DateTime _lastThumbTick;
    private int _thumbVersion;
    private bool _thumbClockWired;

    /// <summary>La transición que muestra un elemento de la galería; nula si no es una transición de imagen.</summary>
    internal static TransitionInstance? GalleryTransition(LibraryElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element.Kind != ItemKind.Transition)
        {
            return null;
        }

        if (EffectRegistry.Find(element.Reference) is { IsTransition: true } definition)
        {
            return BuiltInTransitions.IsAudio(definition.Type) || definition.Type == BuiltInTransitions.Preset
                ? null
                : new TransitionInstance { Type = definition.Type, Duration = GalleryDuration };
        }

        return PackageReference.TryParse(element.Reference, out _)
            ? new TransitionInstance
            {
                Type = BuiltInTransitions.Preset,
                Duration = GalleryDuration,
                Options = { [BuiltInTransitions.PresetOptions.Preset] = element.Reference },
            }
            : null;
    }

    private async void LoadTransitionPreviews()
    {
        _transitionThumbs.Clear();
        StopThumbAnimation();
        if (_services is null || _category != LibraryCategory.Transitions)
        {
            return;
        }

        int version = ++_thumbVersion;
        TransitionPreviews previews = TransitionPreviewHost.For(_services);
        foreach (ElementItem item in Elements.Concat(OwnElements).ToList())
        {
            if (GalleryTransition(item.Element) is not { } transition)
            {
                continue;
            }

            TransitionAnimation? animation = await previews.GetAsync(transition, ThumbWidth, ThumbHeight);
            if (version != _thumbVersion)
            {
                return;
            }

            if (animation is null)
            {
                continue;
            }

            var bitmap = new WriteableBitmap(ThumbWidth, ThumbHeight);
            animation.Bgra[animation.Bgra.Count / 2].CopyTo(bitmap.PixelBuffer);
            bitmap.Invalidate();
            _transitionThumbs[item] = (animation, bitmap);
            item.Preview = bitmap;
        }
    }

    private void OnElementPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ElementItem item } || !_transitionThumbs.ContainsKey(item))
        {
            return;
        }

        if (!_thumbClockWired)
        {
            _thumbClockWired = true;
            _thumbClock.Tick += OnThumbTick;
        }

        StopThumbAnimation();
        _hovered = item;
        _hoverTime = 0;
        _lastThumbTick = DateTime.UtcNow;
        _thumbClock.Start();
    }

    private void OnElementPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ElementItem item } && ReferenceEquals(item, _hovered))
        {
            StopThumbAnimation();
        }
    }

    private void StopThumbAnimation()
    {
        _thumbClock.Stop();
        if (_hovered is { } item && _transitionThumbs.TryGetValue(item, out var thumb))
        {
            Show(thumb.Animation, thumb.Bitmap, thumb.Animation.Bgra.Count / 2);
        }

        _hovered = null;
    }

    private void OnThumbTick(object? sender, object e)
    {
        if (_hovered is not { } item || !_transitionThumbs.TryGetValue(item, out var thumb))
        {
            _thumbClock.Stop();
            return;
        }

        DateTime now = DateTime.UtcNow;
        double cycle = ThumbHoldStart + thumb.Animation.Seconds + ThumbHoldEnd;
        _hoverTime = (_hoverTime + Math.Min(0.1, (now - _lastThumbTick).TotalSeconds)) % cycle;
        _lastThumbTick = now;
        double linear = Math.Clamp((_hoverTime - ThumbHoldStart) / thumb.Animation.Seconds, 0, 1);
        Show(thumb.Animation, thumb.Bitmap, (int)Math.Round(linear * (thumb.Animation.Bgra.Count - 1)));
    }

    private static void Show(TransitionAnimation animation, WriteableBitmap bitmap, int frame)
    {
        animation.Bgra[frame].CopyTo(bitmap.PixelBuffer);
        bitmap.Invalidate();
    }
}
