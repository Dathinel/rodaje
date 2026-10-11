using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Paquetes;

namespace Rodaje.App.Services.Packages;

/// <summary>Lo que lleva el arrastre de una transición: una propia del motor o el preajuste de un paquete.</summary>
/// <param name="Type">Tipo del registro (<see cref="BuiltInTransitions"/>); <see cref="BuiltInTransitions.Preset"/> para las de paquete.</param>
/// <param name="Preset">Referencia <c>paquete@versión/elemento</c> si es de paquete; si no, vacía.</param>
public sealed record TransitionDrag(string Type, string Preset)
{
    /// <summary>
    /// Formato del arrastre desde la biblioteca hacia un corte. El texto es la referencia del elemento: el tipo de una
    /// transición propia (<c>rodaje.wipe</c>) o la referencia de un paquete (<c>rodaje-esenciales@1.0.0/barrido-suave</c>).
    /// El arrastre también lleva <see cref="LibraryElement.DragFormat"/> con el mismo texto.
    /// </summary>
    public const string DragFormat = "rodaje/transicion";

    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TransitionDrag? drag)
    {
        drag = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (EffectRegistry.Find(text) is { IsTransition: true } definition && definition.Type != BuiltInTransitions.Preset)
        {
            drag = new TransitionDrag(definition.Type, "");
            return true;
        }

        if (PackageReference.TryParse(text, out _))
        {
            drag = new TransitionDrag(BuiltInTransitions.Preset, text);
            return true;
        }

        return false;
    }

    /// <summary>El texto que viaja en el arrastre.</summary>
    public override string ToString() => Preset.Length > 0 ? Preset : Type;

    /// <summary>
    /// Pone la transición en el corte donde termina <paramref name="fromClipId"/> en un solo paso del historial (tipo y
    /// preajuste juntos). <see cref="SequenceEditCommand.CreatedIds"/>: la transición creada.
    /// </summary>
    public SequenceEditCommand CreateCommand(string sequenceId, string fromClipId, Time? duration = null) =>
        new("Añadir transición", sequenceId, ctx =>
        {
            string id = TransitionEdits.AddTransition(ctx, fromClipId, Type, duration);
            if (Preset.Length > 0)
            {
                TransitionEdits.SetOption(ctx, id, BuiltInTransitions.PresetOptions.Preset, Preset);
            }

            return (IReadOnlyList<string>)[id];
        });
}
