using Microsoft.UI.Xaml;
using Rodaje.App.Services;
using Rodaje.App.Services.Packages;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Rodaje.App.Views.Timeline;

/// <summary>Soltar un sonido de la biblioteca: su copia entra como clip de audio donde cae (o en la primera pista de audio libre).</summary>
internal sealed partial class TimelineView
{
    private string? _dropSound;
    private PackageSound? _dropSoundInfo;

    private static bool CarriesSound(DataPackageView view) => view.Contains(SoundDrag.DragFormat);

    private async Task ReadDroppedSoundAsync(DragEventArgs e)
    {
        _dropSound = null;
        _dropSoundInfo = null;
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            _dropSound = (await e.DataView.GetDataAsync(SoundDrag.DragFormat))?.ToString();
            if (_dropSound is { } reference && _services is { } services)
            {
                _dropSoundInfo = SoundDrag.Resolve(services.Packages.Installed, reference, out _);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _dropSound = null;
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>La pista (o nula: una nueva) y el instante donde caería el sonido.</summary>
    private (Track? Track, Time At)? PlanSoundDrop(DragEventArgs e)
    {
        if (Session is not { } session || _renderer?.Layout is not { } layout || _dropSoundInfo is not { } sound)
        {
            return null;
        }

        Point p = e.GetPosition(LanesHost);
        Time at = Time.Max(Time.Zero, Viewport.XToTime(p.X)).SnapToFrame(session.Sequence.FrameRate);
        return (SoundDrag.TargetTrack(session.Sequence, layout.RowAt(ContentY(p.Y))?.Track, at, sound.Duration), at);
    }

    private void DragOverSound(DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Sonido";
        if (PlanSoundDrop(e) is { Track: { } track, At: var at } && _dropSoundInfo is { } sound)
        {
            _overlay.Ghost = (track.Id, at, at + sound.Duration);
        }
        else
        {
            ClearDragOverlay();
        }

        Invalidate();
    }

    private async Task DropSoundAsync(DragEventArgs e)
    {
        EditorSession session = Session!;
        e.Handled = true;
        (Track? Track, Time At)? target = PlanSoundDrop(e);
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            _dropSound ??= (await e.DataView.GetDataAsync(SoundDrag.DragFormat))?.ToString();
            if (_dropSound is not { } reference || _services is not { } services)
            {
                return;
            }

            if ((_dropSoundInfo ?? SoundDrag.Resolve(services.Packages.Installed, reference, out _)) is not { } sound)
            {
                session.Notify(NotificationSeverity.Warning, "No se pudo soltar", "El sonido ya no está instalado o su archivo está dañado.");
                return;
            }

            string folder = GeneratedMedia.Ensure(session.FilePath, services.Settings.Current.GeneratedMediaFolder);
            string copy = await Task.Run(() => SoundDrag.CopyInto(sound, folder));
            Time at = target?.At ?? session.ProgramPosition;
            session.InsertSound(copy, sound, at, target?.Track?.Id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            session.Notify(NotificationSeverity.Warning, "No se pudo soltar", ex.Message);
        }
        finally
        {
            _dropSound = null;
            _dropSoundInfo = null;
            ClearDragOverlay();
            Invalidate();
            deferral.Complete();
        }
    }
}
