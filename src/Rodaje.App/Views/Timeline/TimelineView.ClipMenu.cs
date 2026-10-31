using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Shortcuts;
using Rodaje.Core.Effects;
using Rodaje.Core.Editing;
using Windows.Foundation;

namespace Rodaje.App.Views.Timeline;

/// <summary>Menú contextual de un clip: enlazar o desenlazar, separar audio, y copiar y pegar atributos, y análisis de audio.</summary>
internal sealed partial class TimelineView
{
    private static readonly (ClipAttributeKinds Kind, string Text)[] PasteChoices =
    [
        (ClipAttributeKinds.All, "Todo"),
        (ClipAttributeKinds.Effects, "Efectos"),
        (ClipAttributeKinds.Transform, "Transformación"),
        (ClipAttributeKinds.Speed, "Velocidad"),
        (ClipAttributeKinds.Volume, "Volumen"),
        (ClipAttributeKinds.Text, "Estilo del texto"),
        (ClipAttributeKinds.Shadow, "Sombras"),
    ];

    /// <summary>Abre el menú del clip en <paramref name="p"/>; la selección ya incluye el clip pulsado.</summary>
    private void ShowClipMenu(Point p)
    {
        EditorSession session = Session!;
        var menu = new MenuFlyout();

        LinkAction link = session.LinkActionForSelection;
        var toggleLink = new MenuFlyoutItem
        {
            Text = link == LinkAction.Unlink ? "Desenlazar" : "Enlazar",
            IsEnabled = link != LinkAction.None,
        };
        toggleLink.Click += (_, _) => session.ToggleLinkSelection();
        menu.Items.Add(toggleLink);

        var detach = new MenuFlyoutItem { Text = "Separar audio", IsEnabled = session.CanDetachAudio };
        detach.Click += (_, _) => session.DetachAudioOfSelection();
        menu.Items.Add(detach);
        menu.Items.Add(new MenuFlyoutSeparator());

        ShortcutRegistry registry = _services!.ShortcutRegistry;
        var copyClips = new MenuFlyoutItem { Text = "Copiar clips", KeyboardAcceleratorTextOverride = registry.GetKeyText(ActionIds.CopyClips) };
        copyClips.Click += (_, _) => session.CopySelection();
        menu.Items.Add(copyClips);
        var duplicate = new MenuFlyoutItem { Text = "Duplicar clip", KeyboardAcceleratorTextOverride = registry.GetKeyText(ActionIds.DuplicateClips) };
        duplicate.Click += (_, _) => session.DuplicateSelection();
        menu.Items.Add(duplicate);
        var pasteClips = new MenuFlyoutItem
        {
            Text = "Pegar en el cursor",
            IsEnabled = session.CanPaste,
            KeyboardAcceleratorTextOverride = registry.GetKeyText(ActionIds.PasteAtCursor),
        };
        pasteClips.Click += (_, _) => session.PasteAtPlayhead();
        menu.Items.Add(pasteClips);
        menu.Items.Add(new MenuFlyoutSeparator());

        var copy = new MenuFlyoutItem { Text = "Copiar atributos" };
        copy.Click += (_, _) => session.CopyAttributes();
        menu.Items.Add(copy);

        ClipAttributeKinds available = session.CopiedAttributes?.Available ?? ClipAttributeKinds.None;
        var paste = new MenuFlyoutSubItem { Text = "Pegar atributos", IsEnabled = available != ClipAttributeKinds.None };
        bool stillSelection = session.SelectedClipIds.Count > 0 && session.SelectedClipIds.All(id =>
            session.Sequence.FindClip(id) is { Clip: { } clip } && session.Project.FindMedia(clip.MediaId)?.IsStillImage == true);
        foreach ((ClipAttributeKinds kind, string text) in PasteChoices)
        {
            bool applies = kind != ClipAttributeKinds.Speed || !stillSelection;
            var item = new MenuFlyoutItem
            {
                Text = text,
                IsEnabled = (available & kind) != ClipAttributeKinds.None && applies,
            };
            if (!applies)
            {
                ToolTipService.SetToolTip(item, "No aplica a imágenes fijas.");
            }

            item.Click += (_, _) => session.PasteAttributes(kind);
            paste.Items.Add(item);
            if (kind == ClipAttributeKinds.All)
            {
                paste.Items.Add(new MenuFlyoutSeparator());
            }
        }

        menu.Items.Add(paste);
        AddKenBurnsItems(menu);
        AddCompoundItems(menu);
        AddOrganizeItems(menu);
        AddAnalysisItems(menu);
        menu.ShowAt(LanesHost, new FlyoutShowOptions { Position = p });
    }

    private void AddKenBurnsItems(MenuFlyout menu)
    {
        EditorSession session = Session!;
        List<string> photoClips = [.. session.SelectedClipIds.Where(id =>
            session.Sequence.FindClip(id) is { Clip: { } clip } && session.Project.FindMedia(clip.MediaId)?.IsStillImage == true)];
        if (photoClips.Count == 0)
        {
            return;
        }

        var kenBurns = new MenuFlyoutSubItem { Text = "Efecto Ken Burns…" };
        (string Id, string Text)[] choices =
        [
            (MotionPresets.KenBurnsZoomIn, "Acercar"),
            (MotionPresets.KenBurnsZoomOut, "Alejar"),
            (MotionPresets.KenBurnsPanLeft, "Desplazar a la izquierda"),
            (MotionPresets.KenBurnsPanRight, "Desplazar a la derecha"),
        ];

        foreach ((string id, string text) in choices)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => session.Execute(MotionPresetCommands.Apply(session.SequenceId, photoClips, id));
            kenBurns.Items.Add(item);
        }

        menu.Items.Add(kenBurns);
    }
}
