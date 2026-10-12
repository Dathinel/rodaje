using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Views.Inspector.Properties;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Inspector.Masks;

/// <summary>
/// Filas de parámetros de un efecto (las mismas de Propiedades y Filtros) en un panel: arrastrar es un paso del historial,
/// con keyframes, navegación, interpolación y restablecer. Quien la usa dice qué efecto se edita con <see cref="Target"/>.
/// </summary>
internal sealed class EffectRowGroup(Panel host, ClipPropertyEditor editor, Func<(Clip Clip, string Type, string EffectId)?> target,
    Func<XamlRoot?> root, Action changed)
{
    private readonly List<ParameterRow> _rows = [];
    private ParameterRow? _dragRow;
    private string? _type;
    private string[] _names = [];
    private ParameterSpec.Context _context;

    public IReadOnlyList<ParameterRow> Rows => _rows;

    /// <summary>Arma las filas de <paramref name="names"/> (en ese orden) si cambió el tipo, los nombres o el contexto.</summary>
    public void Build(string type, IReadOnlyList<string> names, ParameterSpec.Context context)
    {
        if (_type == type && _context == context && _names.SequenceEqual(names))
        {
            return;
        }

        Finish();
        host.Children.Clear();
        _rows.Clear();
        IReadOnlyList<ParameterSpec> specs = ParameterSpec.ForEffect(type, context);
        foreach (string name in names)
        {
            if (specs.FirstOrDefault(s => s.Name == name) is not { } spec)
            {
                continue;
            }

            var row = new ParameterRow();
            row.Configure(spec);
            row.Changing += OnChanging;
            row.ChangeEnded += OnChangeEnded;
            row.Committed += OnCommitted;
            row.KeyframeToggled += OnKeyframeToggled;
            row.NavigateRequested += OnNavigate;
            row.InterpolationChosen += OnInterpolation;
            row.ResetRequested += OnReset;
            row.KeyframesClearRequested += OnClearKeyframes;
            _rows.Add(row);
            host.Children.Add(row);
        }

        _type = type;
        _names = [.. names];
        _context = context;
    }

    /// <summary>Muestra los valores en el cursor; devuelve cuántos keyframes tienen las filas.</summary>
    public int Show(Clip clip, string effectId)
    {
        bool inside = editor.IsPlayheadInside(clip);
        int keyframes = 0;
        foreach (ParameterRow row in _rows)
        {
            ParameterState state = editor.Read(clip, _type!, row.Spec.Name, row.Spec.DefaultValue, effectId);
            keyframes += state.KeyframeCount;
            row.Show(state, inside);
        }

        return keyframes;
    }

    public void Finish()
    {
        foreach (ParameterRow row in _rows)
        {
            row.FinishChange();
        }
    }

    private void OnChanging(object? sender, double value)
    {
        if (sender is not ParameterRow row || target() is not { } t)
        {
            return;
        }

        if (!ReferenceEquals(_dragRow, row) || !editor.IsDragging)
        {
            _dragRow = row;
            editor.BeginChange(t.Clip, t.Type, row.Spec.Name, row.Spec.Description, t.EffectId);
        }

        editor.Change(value);
    }

    private void OnChangeEnded(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _dragRow))
        {
            return;
        }

        _dragRow = null;
        editor.EndChange();
        changed();
    }

    private void OnCommitted(object? sender, double value)
    {
        if (sender is ParameterRow row && target() is { } t)
        {
            editor.SetValue(t.Clip, t.Type, row.Spec.Name, value, row.Spec.Description, t.EffectId);
        }
    }

    private void OnKeyframeToggled(object? sender, EventArgs e)
    {
        if (sender is ParameterRow row && target() is { } t)
        {
            editor.ToggleKeyframe(t.Clip, t.Type, row.Spec.Name, row.Spec.DefaultValue, t.EffectId);
        }
    }

    private void OnNavigate(object? sender, int direction)
    {
        if (sender is ParameterRow row && target() is { } t)
        {
            editor.GoToKeyframe(t.Clip, t.Type, row.Spec.Name, direction, t.EffectId);
        }
    }

    private void OnInterpolation(object? sender, Interpolation interpolation)
    {
        if (sender is ParameterRow row && target() is { } t)
        {
            editor.SetInterpolation(t.Clip, t.Type, row.Spec.Name, interpolation, t.EffectId);
        }
    }

    private async void OnReset(object? sender, EventArgs e)
    {
        if (sender is ParameterRow row && target() is { } t)
        {
            _dragRow = null;
            await editor.ResetAsync(t.Clip, t.Type, [(row.Spec.Name, row.Spec.DefaultValue)], row.Spec.ResetDescription,
                KeyframeDialog.ForReset(root(), row.Spec.Label), t.EffectId);
        }
    }

    private async void OnClearKeyframes(object? sender, EventArgs e)
    {
        if (sender is ParameterRow row && target() is { } t)
        {
            _dragRow = null;
            string label = row.Spec.Label;
            await editor.ClearKeyframesAsync(t.Clip, t.Type, row.Spec.Name,
                "Quitar keyframes de " + char.ToLowerInvariant(label[0]) + label[1..], KeyframeDialog.ForClear(root(), label), t.EffectId);
        }
    }
}
