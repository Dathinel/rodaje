using Rodaje.App.Services.Crashes;

namespace Rodaje.App.Services.Shortcuts;

/// <summary>Identificadores estables de las acciones del armazón; se guardan en <c>atajos.json</c>, no se renombran.</summary>
public static class ActionIds
{
    public const string NewProject = "file.new";
    public const string OpenProject = "file.open";
    public const string Save = "file.save";
    public const string SaveAs = "file.saveAs";
    public const string Import = "file.import";
    public const string Export = "file.export";
    public const string ExportFrame = "file.exportFrame";

    public const string Undo = "edit.undo";
    public const string Redo = "edit.redo";
    public const string Split = "edit.split";
    public const string SplitAllTracks = "edit.splitAll";
    public const string Delete = "edit.delete";
    public const string TrimStart = "edit.trimStart";
    public const string TrimEnd = "edit.trimEnd";
    public const string SelectAll = "edit.selectAll";
    public const string CopyClips = "edit.copy";
    public const string DuplicateClips = "edit.duplicate";
    public const string PasteAtCursor = "edit.pasteAtCursor";

    public const string ClipSpeed = "clip.speed";

    /// <summary>La transición por defecto en el corte (Ctrl+T); la une <c>TransitionCommands</c>, que tiene los ajustes.</summary>
    public const string AddTransition = "edit.transition";
    public const string FreezeFrame = "clip.freeze";
    public const string SpeedUp = "clip.faster";
    public const string SlowDown = "clip.slower";
    public const string Reverse = "clip.reverse";
    public const string AddText = "clip.addText";
    public const string AddSubtitleTrack = "timeline.subtitleTrack";
    public const string AddVideoTrack = "timeline.videoTrack";
    public const string AddAudioTrack = "timeline.audioTrack";

    public const string TogglePlay = "playback.togglePlay";
    public const string PreviousFrame = "playback.previousFrame";
    public const string NextFrame = "playback.nextFrame";
    public const string GoToStart = "playback.start";
    public const string GoToEnd = "playback.end";
    public const string CaptureFrame = "playback.captureFrame";
    public const string GoToTime = "playback.goToTime";

    // Las registra el reproductor; aquí para que la vista esencial de la página de atajos las nombre.
    public const string ShuttleReverse = "playback.shuttleReverse";
    public const string ShuttleStop = "playback.shuttleStop";
    public const string ShuttleForward = "playback.shuttleForward";

    public const string AddMarker = "timeline.marker";
    public const string AddRegion = "timeline.region";
    public const string ZoomIn = "timeline.zoomIn";
    public const string ZoomOut = "timeline.zoomOut";
    public const string ZoomToFit = "timeline.zoomFit";
    public const string ZoomToSelection = "timeline.zoomSelection";
    public const string ToggleSnapping = "timeline.snapping";
    public const string ToggleAutoRipple = "timeline.autoRipple";
    public const string ToggleMagneticMain = "timeline.magneticMain";

    public const string NormalTool = "tool.normal";
    public const string EnvelopeTool = "tool.envelope";
    public const string SelectionTool = "tool.selection";
    public const string ZoomTool = "tool.zoom";
    public const string RollTool = "tool.roll";
    public const string SlideTool = "tool.slide";
    public const string BladeTool = "tool.blade";
    public const string RippleTool = "tool.ripple";
    public const string SlipTool = "tool.slip";
    public const string RangeTool = "tool.range";

    public const string ShowShortcuts = "view.shortcuts";
    public const string ShowPlugins = "view.plugins";
    public const string ShowMixer = "view.mixer";
    public const string ShowHistory = "view.history";
    public const string LayoutBalanced = "view.layoutBalanced";
    public const string LayoutMoreViewer = "view.layoutMoreViewer";
    public const string LayoutMoreTimeline = "view.layoutMoreTimeline";
    public const string ShowPreferences = "view.preferences";
}
