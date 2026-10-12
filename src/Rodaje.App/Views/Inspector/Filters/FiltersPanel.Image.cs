using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services.Inspector;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using K = Rodaje.Core.Model.BuiltInEffects.ChromaKeyParameters;

namespace Rodaje.App.Views.Inspector.Filters;

/// <summary>
/// Lo propio de recortar, croma, ruedas y curvas en Filtros: la proporción (y las asas del visor mientras el recorte está
/// elegido), el cuentagotas, las tres ruedas y el editor de curvas. Un arrastre es un paso del historial.
/// </summary>
internal sealed partial class FiltersPanel
{
    private bool _imageGesture;

    /// <summary>Los parámetros que se editan con un control propio en vez de con una fila.</summary>
    private static bool HidesRow(string type, string name) => type switch
    {
        BuiltInEffects.ChromaKey => name is K.Red or K.Green or K.Blue,
        BuiltInEffects.ColorWheels => WheelMath.Wheels.Any(w => name == w.Red || name == w.Green || name == w.Blue),
        BuiltInEffects.Compressor => true,
        _ => false,
    };

    private void ShowImageOptions(Clip clip, EffectInstance effect)
    {
        Time at = clip.ToEffectTime(_services!.Session.Position);
        double Value(string name) => effect.Parameters.TryGetValue(name, out Parameter? p) ? p.Evaluate(at)
            : EffectRegistry.DefaultValue(effect.Type, name);

        AspectRow.Visibility = effect.Type == BuiltInEffects.Crop ? Visibility.Visible : Visibility.Collapsed;
        if (effect.Type == BuiltInEffects.Crop)
        {
            _updating = true;
            if (AspectBox.Items.Count == 0)
            {
                foreach (string aspect in EffectRegistry.CropAspects)
                {
                    AspectBox.Items.Add(EffectUi.ChoiceName(BuiltInEffects.CropOptions.Aspect, aspect));
                }
            }

            AspectBox.SelectedIndex = Math.Max(0, EffectRegistry.CropAspects.IndexOf(effect.Options.GetValueOrDefault(BuiltInEffects.CropOptions.Aspect, "free")));
            _updating = false;
        }

        KeyRow.Visibility = effect.Type == BuiltInEffects.ChromaKey ? Visibility.Visible : Visibility.Collapsed;
        if (effect.Type == BuiltInEffects.ChromaKey)
        {
            static byte B(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
            KeySwatch.Background = new SolidColorBrush(ColorHelper.FromArgb(255, B(Value(K.Red)), B(Value(K.Green)), B(Value(K.Blue))));
        }

        Wheels.Visibility = effect.Type == BuiltInEffects.ColorWheels ? Visibility.Visible : Visibility.Collapsed;
        if (effect.Type == BuiltInEffects.ColorWheels)
        {
            Wheels.Show([.. WheelMath.Wheels.Select(w => WheelMath.Position(Value(w.Red), Value(w.Green), Value(w.Blue), w.Scale))]);
        }

        CurveEditor.Visibility = effect.Type == BuiltInEffects.Curves ? Visibility.Visible : Visibility.Collapsed;
        if (effect.Type == BuiltInEffects.Curves)
        {
            CurveEditor.Show(effect.Options);
        }
    }

    /// <summary>Con Filtros a la vista y un recorte elegido, el visor muestra la imagen entera con sus asas.</summary>
    private void PublishCropEditing()
    {
        if (_services is null)
        {
            return;
        }

        string? cropId = Visibility == Visibility.Visible && CurrentClip() is { } clip && _effectId is { } id
            && clip.Effects.Find(e => e.Id == id) is { Type: BuiltInEffects.Crop, Enabled: true } ? id : null;
        _services.Session.SetCropEditing(cropId);
    }

    private void OnAspectSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || AspectBox.SelectedIndex < 0 || _services is null || _editor is null || CurrentClip() is not { } clip
            || _effectId is not { } effectId || clip.Effects.Find(x => x.Id == effectId) is not { } effect)
        {
            return;
        }

        string aspect = EffectRegistry.CropAspects[AspectBox.SelectedIndex];
        double? ratio = CropGeometry.Ratio(aspect);
        Time at = _editor.IsPlayheadInside(clip) ? _editor.KeyframeTimeAtPlayhead(clip) : clip.ToEffectTime(clip.Start);
        CropRect current = CropGeometry.Evaluate(effect, at);
        CropRect? fitted = ratio is { } r && _services.Session.Project.FindMedia(clip.MediaId) is { Width: > 0, Height: > 0 } media
            ? CropGeometry.FitAspect(current, r, media.Width, media.Height)
            : null;
        string clipId = clip.Id;
        _services.Session.Execute(new SequenceEditCommand("Proporción del recorte", _services.Session.SequenceId, ctx =>
        {
            EffectEdits.SetOption(ctx, clipId, effectId, BuiltInEffects.CropOptions.Aspect, aspect);
            if (fitted is { } crop)
            {
                Player.PlayerView.SetCrop(ctx, clipId, effectId, crop, at);
            }
        }));
    }

    private void OnPickKeyClick(object sender, RoutedEventArgs e)
    {
        if (_services is not null && _clipId is not null && _effectId is not null)
        {
            _services.Session.RequestKeyPick(_clipId, _effectId);
        }
    }

    private void OnWheelChanged(object? sender, WheelChange change)
    {
        if (_services is null || _editor is null || CurrentClip() is not { } clip || _effectId is not { } effectId)
        {
            return;
        }

        (string red, string green, string blue, double scale) = WheelMath.Wheels[change.Wheel];
        (double r, double g, double b) = WheelMath.Tint(change.X, change.Y, scale);
        Time at = _editor.IsPlayheadInside(clip) ? _editor.KeyframeTimeAtPlayhead(clip) : Time.Zero;
        string clipId = clip.Id;
        RunImageEdit("Ruedas de color", $"ruedas|{effectId}|{change.Wheel}|{at.Ticks}", change.Final, ctx =>
        {
            EffectEdits.SetParameter(ctx, clipId, effectId, red, Math.Round(r, 4), at);
            EffectEdits.SetParameter(ctx, clipId, effectId, green, Math.Round(g, 4), at);
            EffectEdits.SetParameter(ctx, clipId, effectId, blue, Math.Round(b, 4), at);
        });
    }

    private void OnCurveChanged(object? sender, (string Option, string Text, bool Final) change)
    {
        if (_services is null || _clipId is not { } clipId || _effectId is not { } effectId)
        {
            return;
        }

        RunImageEdit("Cambiar la curva", $"curva|{effectId}|{change.Option}", change.Final,
            ctx => EffectEdits.SetOption(ctx, clipId, effectId, change.Option, change.Text));
    }

    /// <summary>Un arrastre de rueda o de curva: abre un gesto al primer cambio y lo cierra al soltar.</summary>
    private void RunImageEdit(string description, string mergeKey, bool final, Action<EditContext> edit)
    {
        if (_services is null)
        {
            return;
        }

        if (!_imageGesture && !final)
        {
            _imageGesture = true;
            _services.Session.BeginGesture(description);
        }

        _services.Session.Execute(new SequenceEditCommand(description, _services.Session.SequenceId, edit, mergeKey: mergeKey));
        if (final && _imageGesture)
        {
            _imageGesture = false;
            _services.Session.EndGesture();
        }
    }
}
