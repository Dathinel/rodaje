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
    private const string GroupId = "clip.group";
    private const string UngroupId = "clip.ungroup";
    private const string NudgeLeftId = "clip.nudgeLeft";
    private const string NudgeRightId = "clip.nudgeRight";
    private const string CloseGapsId = "timeline.closeGaps";
    private const string NextGapId = "timeline.nextGap";
    private const string PreviousGapId = "timeline.previousGap";
    private const string ReplaceId = "edit.replaceSource";
    private const string FitToFillId = "edit.fitToFillSource";
    private const string SuperimposeId = "edit.superimposeSource";
    private const string AngleIdPrefix = "multicam.angle";

    private void RegisterProActions(EditorServices services)
    {
        ShortcutRegistry registry = services.ShortcutRegistry;
        registry.Register(new ShortcutAction(GroupId, "Agrupar", ShortcutGroups.Edit, ["Ctrl+G"], agile: []) { Description = "Junta los clips elegidos: se seleccionan y se mueven juntos, aunque estén en la misma pista." });
        registry.Register(new ShortcutAction(UngroupId, "Desagrupar", ShortcutGroups.Edit, ["Ctrl+Mayús+G"], agile: []) { Description = "Separa los clips del grupo; los enlaces de audio y video se mantienen." });
        registry.Register(new ShortcutAction(NudgeLeftId, "Mover clip un fotograma a la izquierda", ShortcutGroups.Edit, ["Alt+←"], classic: ["Ctrl+Alt+←"]) { ShortLabel = "Empujar ←", Repeats = true, Description = "Corre la selección (con sus enlazados y su grupo) un fotograma, sin pisar nada." });
        registry.Register(new ShortcutAction(NudgeRightId, "Mover clip un fotograma a la derecha", ShortcutGroups.Edit, ["Alt+→"], classic: ["Ctrl+Alt+→"]) { ShortLabel = "Empujar →", Repeats = true, Description = "Corre la selección (con sus enlazados y su grupo) un fotograma, sin pisar nada." });
        registry.Register(new ShortcutAction(CloseGapsId, "Cerrar todos los huecos", ShortcutGroups.Timeline, []) { ShortLabel = "Sin huecos", Description = "Junta los clips de la pista elegida (o de la principal) sin desincronizar los enlazados." });
        registry.Register(new ShortcutAction(NextGapId, "Ir al hueco siguiente", ShortcutGroups.Timeline, []) { ShortLabel = "Hueco →", Description = "Lleva el cursor al próximo tramo vacío de cualquier pista." });
        registry.Register(new ShortcutAction(PreviousGapId, "Ir al hueco anterior", ShortcutGroups.Timeline, []) { ShortLabel = "← Hueco", Description = "Lleva el cursor al tramo vacío anterior de cualquier pista." });
        registry.Register(new ShortcutAction(ReplaceId, "Reemplazar desde el origen", ShortcutGroups.Edit, ["Mayús+."]) { ShortLabel = "Reemplazar", Description = "Cambia el clip bajo el cursor por el origen, en su lugar y con su duración, alineando los dos cursores." });
        registry.Register(new ShortcutAction(FitToFillId, "Ajustar a rellenar desde el origen", ShortcutGroups.Edit, ["Ctrl+Mayús+."]) { ShortLabel = "Ajustar", Description = "El tramo del origen llena el clip bajo el cursor (o las marcas de la secuencia) cambiando la velocidad." });
        registry.Register(new ShortcutAction(SuperimposeId, "Superponer desde el origen", ShortcutGroups.Edit, ["Mayús+,"]) { ShortLabel = "Superponer", Description = "Pone la imagen del origen encima, en la primera pista de video libre." });
        for (int i = 1; i <= 9; i++)
        {
            registry.Register(new ShortcutAction(AngleIdPrefix + i, $"Ángulo {i} de la multicámara", ShortcutGroups.Edit, [i.ToString(System.Globalization.CultureInfo.InvariantCulture)]) { ShortLabel = $"Ángulo {i}", Description = "Corta en el cursor al ángulo con ese número; también mientras se reproduce." });
        }

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
        var group = new MenuFlyoutItem { Text = WithKey(registry, "Agrupar", GroupId), IsEnabled = session.SelectedClipIds.Count > 1 };
        group.Click += (_, _) => session.GroupSelection();
        menu.Items.Add(group);
        bool grouped = session.SelectedClipIds.Any(id => session.Sequence.FindClip(id)?.Clip.GroupId is not null);
        var ungroup = new MenuFlyoutItem { Text = WithKey(registry, "Desagrupar", UngroupId), IsEnabled = grouped };
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

        var gaps = new MenuFlyoutItem { Text = "Cerrar todos los huecos de la pista" };
        gaps.Click += (_, _) => session.CloseAllGaps();
        menu.Items.Add(gaps);
    }

    private static string WithKey(ShortcutRegistry registry, string text, string actionId) =>
        registry.GetKeyText(actionId) is { Length: > 0 } key ? $"{text} ({key})" : text;

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
