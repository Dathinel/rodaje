using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services.Inspector;
using Windows.Globalization.NumberFormatting;

namespace Rodaje.App.Views.Inspector;

/// <summary>Lo escrito en un <c>NumberBox</c> se confirma antes de que el foco salga de él.</summary>
/// <remarks>
/// El <c>NumberBox</c> confirma el texto en su <c>LostFocus</c>, que llega después del clic que movió el foco. Si ese clic
/// eligió otro clip en la línea de tiempo, el panel ya se había refrescado y el valor iba al clip nuevo o se perdía
/// (hallazgo 31 de la auditoría del 24-oct). <c>LosingFocus</c> llega antes, dentro de la llamada que mueve el foco: al poner
/// ahí el valor, su <c>ValueChanged</c> lo aplica a los clips que se estaban editando.
/// </remarks>
internal static class NumberBoxCommit
{
    public static void CommitBeforeFocusLeaves(NumberBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        box.LosingFocus += (_, e) =>
        {
            if (e.OldFocusedElement is TextBox text && box.NumberFormatter is INumberParser parser
                && TypedNumber.Pending(text.Text, box.Value, parser.ParseDouble) is { } value)
            {
                box.Value = value;
            }
        };
    }
}
