using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.App.Services.MotionTemplates;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Packages.Content;

namespace Rodaje.App.Services;

/// <summary>Plantillas de movimiento de títulos (D-064): ponerlas en la línea de tiempo, cambiar un valor, actualizarlas y convertirlas en títulos.</summary>
public sealed partial class EditorSession
{
    /// <summary>El primer clip de plantilla seleccionado, si hay.</summary>
    public Clip? SelectedTemplate => SelectedTitle is { Generator.Template: not null } clip ? clip : null;

    /// <summary>
    /// Pone la plantilla con sus valores por defecto y su duración en <paramref name="at"/> (nulo: el cursor), en la pista
    /// dada o en la de video libre más alta, la selecciona y pide la pestaña «Texto». Un paso del historial.
    /// </summary>
    public bool AddTemplate(MotionTemplateEntry entry, string? trackId = null, Time? at = null)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(entry);
        ClipGenerator generator;
        try
        {
            generator = TemplateInstances.NewGenerator(entry);
        }
        catch (PackageContentException ex)
        {
            Notify(NotificationSeverity.Warning, $"No se pudo usar «{entry.Name}»", ex.Message);
            return false;
        }

        Time start = at ?? EditPosition;
        Time duration = Time.FromSeconds(entry.Document.DurationSeconds).SnapToFrame(Sequence.FrameRate);
        SequenceEditCommand command = trackId is null
            ? Edits.InsertTitle(SequenceId, start, duration, generator, UniqueTrackName("Texto"), _ids)
            : Edits.InsertGenerator(SequenceId, trackId, start, duration, generator, _ids);
        if (!Execute(command))
        {
            return false;
        }

        Select([command.CreatedIds[0]]);
        ShowAfterEntry(start, Time.FromSeconds(generator.Template!.Layers.Max(l => l.Start + (l.Style.In?.DurationSeconds ?? 0))));
        TextPanelRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Cambia un valor de la plantilla del clip y vuelve a instanciar sus capas: un paso del historial. Los cambios seguidos
    /// del mismo parámetro (teclear, un deslizador) se funden hasta <see cref="BreakMerge"/>.
    /// </summary>
    /// <returns><c>false</c> si el valor no vale para ese parámetro o el clip ya no es una plantilla; la sesión avisa.</returns>
    public bool SetTemplateValue(string clipId, string parameterId, JsonNode value)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(value);
        if (Sequence.FindClip(clipId) is not { Clip.Generator.Template: { } template })
        {
            return false;
        }

        try
        {
            TemplateDocument document = ContentReaders.ReadTemplate(template.Document);
            // Salir de un campo sin tocarlo no es un paso del historial.
            if (document.Parameters.FirstOrDefault(p => p.Id == parameterId) is { } parameter
                && JsonNode.DeepEquals(TemplateInstances.Current(document, template.Values, parameter), value))
            {
                return false;
            }

            Dictionary<string, JsonElement> values = TemplateInstances.With(template.Values, parameterId, value);
            return Execute(Edits.SetTemplateValues(SequenceId, clipId, values, TemplateInstances.Layers(document, values, template.Source), _ids,
                parameterId));
        }
        catch (PackageContentException ex)
        {
            Notify(NotificationSeverity.Warning, "No se pudo cambiar la plantilla", ex.Message);
            return false;
        }
    }

    /// <summary>«Actualizar a X»: reaplica la versión instalada más nueva conservando los valores que sigan valiendo.</summary>
    /// <returns>Los parámetros que volvieron a su defecto; nulo si no se pudo.</returns>
    public IReadOnlyList<string>? UpdateTemplate(string clipId, MotionTemplateEntry newer)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(newer);
        if (Sequence.FindClip(clipId) is not { Clip.Generator.Template: { } template })
        {
            return null;
        }

        try
        {
            (GeneratorTemplate updated, IReadOnlyList<string> reset) = TemplateInstances.Update(template, newer);
            return Execute(Edits.ReplaceTemplate(SequenceId, clipId, updated, _ids)) ? reset : null;
        }
        catch (PackageContentException ex)
        {
            Notify(NotificationSeverity.Warning, "No se pudo actualizar la plantilla", ex.Message);
            return null;
        }
    }

    /// <summary>«Convertir en títulos»: un título por capa, seleccionados. Un paso del historial.</summary>
    public bool ConvertTemplateToTitles(string clipId)
    {
        VerifyAccess();
        SequenceEditCommand command = Edits.ConvertTemplateToTitles(SequenceId, clipId, _ids);
        if (!Execute(command))
        {
            return false;
        }

        Sequence sequence = Sequence;
        Select(command.CreatedIds.Where(id => sequence.FindClip(id) is not null));
        return true;
    }
}
