using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Rodaje.App.Services;
using Rodaje.Core.Editing;
using Windows.Foundation;

namespace Rodaje.App.Views.Timeline;

/// <summary>Menú contextual de un clip: enlazar o desenlazar, separar audio, y copiar y pegar atributos.</summary>
internal sealed partial class TimelineView
{
    private static readonly (ClipAttributeKinds Kind, string Text)[] PasteChoices =
    [
        (ClipAttributeKinds.All, "Todo"),
        (ClipAttributeKinds.Effects, "Efectos"),
        (ClipAttributeKinds.Transform, "Transformación"),
        (ClipAttributeKinds.Speed, "Velocidad"),
        (ClipAttributeKinds.Volume, "Volumen"),
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

        var copy = new MenuFlyoutItem { Text = "Copiar atributos" };
        copy.Click += (_, _) => session.CopyAttributes();
        menu.Items.Add(copy);

        ClipAttributeKinds available = session.CopiedAttributes?.Available ?? ClipAttributeKinds.None;
        var paste = new MenuFlyoutSubItem { Text = "Pegar atributos", IsEnabled = available != ClipAttributeKinds.None };
        foreach ((ClipAttributeKinds kind, string text) in PasteChoices)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = (available & kind) != ClipAttributeKinds.None };
            item.Click += (_, _) => session.PasteAttributes(kind);
            paste.Items.Add(item);
            if (kind == ClipAttributeKinds.All)
            {
                paste.Items.Add(new MenuFlyoutSeparator());
            }
        }

        menu.Items.Add(paste);
        menu.ShowAt(LanesHost, new FlyoutShowOptions { Position = p });
    }
}
