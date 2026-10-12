using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;
using Q = Rodaje.Core.Model.BuiltInEffects.HslQualifierParameters;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Los cuentagotas de color además del croma: balance de blancos (temperatura y tinte de «Color y LUT» que dejan gris el
/// punto, con la exposición, el contraste y la saturación que ya tiene) y el tono del calificador HSL. Un paso cada uno; con
/// keyframes, el valor va al keyframe del cursor.
/// </summary>
internal sealed partial class PlayerView
{
    private void ApplyColorPick((string ClipId, string EffectId, ColorPick Purpose) target, (double R, double G, double B) color)
    {
        if (Session is null || Session.Sequence.FindClip(target.ClipId) is not { } found
            || found.Clip.Effects.Find(e => e.Id == target.EffectId) is not { } effect || _properties is null)
        {
            return;
        }

        Clip clip = found.Clip;
        Time at = _properties.KeyframeTimeAtPlayhead(clip);
        double Value(string name) => effect.Parameters.TryGetValue(name, out Parameter? p) ? p.Evaluate(at)
            : Core.Effects.EffectRegistry.DefaultValue(effect.Type, name);

        if (target.Purpose == ColorPick.WhiteBalance)
        {
            (double temperature, double tint) = ColorPickMath.WhiteBalance(color, Value(C.Exposure), Value(C.Contrast), Value(C.Saturation));
            Session.Execute(new SequenceEditCommand("Balance de blancos con cuentagotas", Session.SequenceId, ctx =>
            {
                EffectEdits.SetParameter(ctx, target.ClipId, target.EffectId, C.Temperature, Math.Round(temperature, 4), at);
                EffectEdits.SetParameter(ctx, target.ClipId, target.EffectId, C.Tint, Math.Round(tint, 4), at);
            }));
            return;
        }

        (double hue, double saturation, double luma) = ColorPickMath.Qualify(color);
        Session.Execute(new SequenceEditCommand("Elegir el color de la corrección secundaria", Session.SequenceId, ctx =>
        {
            EffectEdits.SetParameter(ctx, target.ClipId, target.EffectId, Q.HueCenter, Math.Round(hue, 1), at);
            EffectEdits.SetParameter(ctx, target.ClipId, target.EffectId, Q.SaturationLow, Math.Round(Math.Max(0, saturation * 0.5), 3), at);
            EffectEdits.SetParameter(ctx, target.ClipId, target.EffectId, Q.LumaLow, Math.Round(Math.Max(0, luma - 0.35), 3), at);
            EffectEdits.SetParameter(ctx, target.ClipId, target.EffectId, Q.LumaHigh, Math.Round(Math.Min(1, luma + 0.35), 3), at);
        }));
    }
}
