using Microsoft.UI.Xaml;
using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.MotionTemplates;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Titles;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Packages;
using Rodaje.Packages.Content;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Rodaje.App.Views.Timeline;

/// <summary>Soltar un estilo de texto de la biblioteca: un título nuevo de 5 s donde cae (o en la pista libre más alta).</summary>
internal sealed partial class TimelineView
{
    private string? _dropTitle;

    private static bool CarriesTitle(DataPackageView view) => view.Contains(TitleDrag.DragFormat);

    private async Task ReadDroppedTitleAsync(DragEventArgs e)
    {
        _dropTitle = null;
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            _dropTitle = (await e.DataView.GetDataAsync(TitleDrag.DragFormat))?.ToString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _dropTitle = null;
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>La pista (o nula: la libre más alta) y el instante donde caería el título.</summary>
    private (Track? Track, Time At)? PlanTitleDrop(DragEventArgs e)
    {
        if (Session is not { } session || _renderer?.Layout is not { } layout)
        {
            return null;
        }

        Point p = e.GetPosition(LanesHost);
        Time at = Time.Max(Time.Zero, Viewport.XToTime(p.X)).SnapToFrame(session.Sequence.FrameRate);
        return (TitleDrag.TargetTrack(session.Sequence, layout.RowAt(ContentY(p.Y))?.Track, at), at);
    }

    private void DragOverTitle(DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Texto";
        if (PlanTitleDrop(e) is { Track: { } track, At: var at })
        {
            _overlay.Ghost = (track.Id, at, at + TitleEdits.DefaultDuration);
        }
        else
        {
            ClearDragOverlay();
        }

        Invalidate();
    }

    private async Task DropTitleAsync(DragEventArgs e)
    {
        EditorSession session = Session!;
        e.Handled = true;
        (Track? Track, Time At)? target = PlanTitleDrop(e);
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            _dropTitle ??= (await e.DataView.GetDataAsync(TitleDrag.DragFormat))?.ToString();
            if (_dropTitle is not { } reference || target is not { } t || _services is not { } services)
            {
                return;
            }

            // Una plantilla de movimiento de títulos se suelta igual que un estilo (D-064).
            PackageScan installed = services.Packages.Installed;
            string droppedOn = session.SequenceId;
            if (PackageReference.TryParse(reference, out PackageReference? parsed)
                && installed.Find(parsed.PackageId)?.Manifest.FindItem(parsed.ItemId, ItemKind.MotionTemplate) is not null)
            {
                if (await Task.Run(() => MotionTemplateCatalog.Find(installed, reference)) is not { } template || session.SequenceId != droppedOn)
                {
                    return;
                }

                if (template.Kind != TemplateKind.Title)
                {
                    session.Notify(NotificationSeverity.Informational, $"«{template.Name}» no se suelta en una pista",
                        template.Kind == TemplateKind.Transition
                            ? "Es una plantilla de transición: selecciona un clip pegado a otro y haz doble clic en ella."
                            : "Es una plantilla de efecto: selecciona un clip de video y haz doble clic en ella.");
                    return;
                }

                // La pista bajo el puntero se eligió con la duración de un título; si la plantilla dura más y no cabe, va a la libre más alta.
                session.AddTemplate(template, TemplateDrops.FitsIn(t.Track, t.At, template.Document.DurationSeconds) ? t.Track!.Id : null, t.At);

                return;
            }

            bool builtIn = reference == TitleStyles.BuiltInReference;
            var element = new LibraryElement("Texto", "", "", reference, true, builtIn ? null : PackageOrigin.Installed, ItemKind.TextStyle);
            var applier = new ElementApplier(session, new InstalledContentLoader(() => services.Packages.Installed));
            string sequenceId = session.SequenceId;
            if (await applier.LoadTextStyleAsync(element) is { } style && session.SequenceId == sequenceId)
            {
                session.AddTitleAt(t.Track?.Id, t.At, style);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            session.Notify(NotificationSeverity.Warning, "No se pudo soltar", ex.Message);
        }
        finally
        {
            _dropTitle = null;
            ClearDragOverlay();
            Invalidate();
            deferral.Complete();
        }
    }
}
