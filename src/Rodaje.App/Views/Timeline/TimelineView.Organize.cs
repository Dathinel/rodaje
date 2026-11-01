using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;
using Rodaje.App.Views.Markers;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

/// <summary>
/// La parte profesional de la línea de tiempo: grupos, colores, empujones cuadro a cuadro, huecos, sincronía de
/// enlazados, alto de pista, la cuchilla y el rango, y cortar entre ángulos de una multicámara con 1 a 9.
/// </summary>
internal sealed partial class TimelineView
{
    private const string GroupId = TimelineShortcuts.Group;
    private const string UngroupId = TimelineShortcuts.Ungroup;
    private const string NudgeLeftId = TimelineShortcuts.NudgeLeft;
    private const string NudgeRightId = TimelineShortcuts.NudgeRight;
    private const string CloseGapsId = TimelineShortcuts.CloseGaps;
    private const string NextGapId = TimelineShortcuts.NextGap;
    private const string PreviousGapId = TimelineShortcuts.PreviousGap;
    private const string ReplaceId = TimelineShortcuts.ReplaceSource;
    private const string FitToFillId = TimelineShortcuts.FitToFillSource;
    private const string SuperimposeId = TimelineShortcuts.SuperimposeSource;
    private const string AngleIdPrefix = TimelineShortcuts.AnglePrefix;

    private void RegisterProActions(EditorServices services)
    {
        ShortcutRouter router = services.Shortcuts;
        router.Bind(GroupId, () => Session!.GroupSelection(), () => Session?.SelectedClipIds.Count > 1);
        router.Bind(UngroupId, () => Session!.UngroupSelection(), () => Session?.SelectedClipIds.Count > 0);
        router.Bind(NudgeLeftId, () => Session!.NudgeSelection(-1), () => Session?.SelectedClipIds.Count > 0);
        router.Bind(NudgeRightId, () => Session!.NudgeSelection(1), () => Session?.SelectedClipIds.Count > 0);
        router.Bind(CloseGapsId, () => Session!.CloseAllGaps());
        router.Bind(NextGapId, () => Session!.GoToGap(forward: true));
        router.Bind(PreviousGapId, () => Session!.GoToGap(forward: false));
        router.Bind(ReplaceId, () => Session!.SourceAction(ThreePointAction.Replace), () => Session?.SourceMediaId is not null);
        router.Bind(FitToFillId, () => Session!.SourceAction(ThreePointAction.FitToFill), () => Session?.SourceMediaId is not null);
        router.Bind(SuperimposeId, () => Session!.SourceAction(ThreePointAction.Superimpose), () => Session?.SourceMediaId is not null);
        for (int i = 0; i < 9; i++)
        {
            int angle = i;
            router.Bind(AngleIdPrefix + (i + 1), () => SwitchAngle(angle), () => CurrentMulticam() is { } group && angle < group.Count);
        }

        services.Session.SelectedEdgeChanged += (_, _) => ShowSelectedEdge();
    }

    /// <summary>La multicámara del clip de la pista principal bajo el cursor, si es un ángulo de alguna.</summary>
    private IReadOnlyList<MulticamAngle>? CurrentMulticam()
    {
        EditorSession? session = Session;
        if (session?.MainTrack?.ClipAt(session.EditPosition) is not { } clip)
        {
            return null;
        }

        return session.Multicams.FirstOrDefault(g => MulticamEdits.IndexOf(g, clip.MediaId) >= 0);
    }

    private void SwitchAngle(int angle)
    {
        if (CurrentMulticam() is { } group)
        {
            Session!.SwitchAngle(group, angle);
        }
    }

    private void OnTrackHeightChanging(string trackId, int? height)
    {
        EditorSession session = Session!;
        if (!session.IsInGesture)
        {
            session.BeginGesture("Alto de pista");
        }

        session.TryExecute(Edits.SetTrackHeight(session.SequenceId, trackId, height));
        RebuildLayout();
    }

    /// <summary>Colores, grupos, sincronía y huecos en el menú del clip.</summary>
    private void AddOrganizeItems(MenuFlyout menu)
    {
        EditorSession session = Session!;
        menu.Items.Add(new MenuFlyoutSeparator());
        var color = new MenuFlyoutSubItem { Text = "Color" };
        string? current = session.SelectedClipIds.Select(id => session.Sequence.FindClip(id)?.Clip.Color).FirstOrDefault();
        foreach ((string name, string? hex) in MarkersPanel.Palette)
        {
            var item = new RadioMenuFlyoutItem { Text = name, GroupName = "color-clip", IsChecked = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => session.SetSelectionColor(hex);
            color.Items.Add(item);
        }

        menu.Items.Add(color);
        ShortcutRegistry registry = _services!.ShortcutRegistry;
        var group = new MenuFlyoutItem { Text = "Agrupar", IsEnabled = session.SelectedClipIds.Count > 1, KeyboardAcceleratorTextOverride = registry.GetKeyText(GroupId) };
        group.Click += (_, _) => session.GroupSelection();
        menu.Items.Add(group);
        bool grouped = session.SelectedClipIds.Any(id => session.Sequence.FindClip(id)?.Clip.GroupId is not null);
        var ungroup = new MenuFlyoutItem { Text = "Desagrupar", IsEnabled = grouped, KeyboardAcceleratorTextOverride = registry.GetKeyText(UngroupId) };
        ungroup.Click += (_, _) => session.UngroupSelection();
        menu.Items.Add(ungroup);

        IReadOnlyList<SyncOffset> desync = LinkSync.Find(session.Sequence);
        if (desync.FirstOrDefault(o => session.SelectedClipIds.Contains(o.ClipId)) is { ClipId: not null } offset)
        {
            string frames = Rodaje.App.Services.Titles.ClipLabels.SyncFrames(offset.Frames(session.Sequence.FrameRate));
            var resync = new MenuFlyoutSubItem { Text = $"Volver a sincronizar ({frames} fotogramas)" };
            var move = new MenuFlyoutItem { Text = "Mover a su lugar" };
            move.Click += (_, _) => session.Resync(offset.ClipId, ResyncMode.Move);
            var slip = new MenuFlyoutItem { Text = "Deslizar su contenido" };
            slip.Click += (_, _) => session.Resync(offset.ClipId, ResyncMode.Slip);
            resync.Items.Add(move);
            resync.Items.Add(slip);
            menu.Items.Add(resync);
        }

        var gaps = new MenuFlyoutItem { Text = "Cerrar todos los huecos de la pista", KeyboardAcceleratorTextOverride = registry.GetKeyText(CloseGapsId) };
        gaps.Click += (_, _) => session.CloseAllGaps();
        menu.Items.Add(gaps);
    }

    /// <summary>El borde elegido se marca con la línea de corte de la pista.</summary>
    private void ShowSelectedEdge()
    {
        EditorSession? session = Session;
        if (session?.SelectedEdge is { } edge && session.Sequence.FindClip(edge.ClipId) is { } found)
        {
            _overlay.InsertLine = (found.Track.Id, edge.Edge == TrimEdge.In ? found.Clip.Start : found.Clip.End);
        }
        else if (_mode == PointerMode.None)
        {
            _overlay.InsertLine = null;
        }

        Invalidate();
    }

    /// <summary>Cuchilla: corta el clip pulsado (con sus enlazados) en el cuadro del puntero; con Mayús, todas las pistas.</summary>
    private void BladeAt(TimelineHit hit, bool allTracks)
    {
        EditorSession session = Session!;
        Time at = BladeTime(hit.Time);
        if (allTracks)
        {
            session.Execute(Edits.SplitAllTracks(session.SequenceId, at));
        }
        else if (hit.Clip is { } clip && clip.Start < at && at < clip.End)
        {
            session.Execute(Edits.Split(session.SequenceId, clip.Id, at));
        }
    }

    /// <summary>El cuadro del puntero, pegado al cursor o a un marcador cercano si el ajuste está activo.</summary>
    private Time BladeTime(Time pointer)
    {
        EditorSession session = Session!;
        Time at = pointer.SnapToFrame(session.Sequence.FrameRate);
        if (State.Snapping)
        {
            Time threshold = Time.FromSeconds(SnapPoints.ThresholdPixels / Viewport.PixelsPerSecond);
            SnapPoints snaps = SnapPoints.Collect(session.Sequence, new HashSet<string>(), session.ProgramPosition);
            if (snaps.Nearest(at, threshold) is { } snapped)
            {
                at = snapped;
            }
        }

        return at;
    }
}
