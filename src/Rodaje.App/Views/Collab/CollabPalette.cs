using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services.Collab;
using Windows.UI;

namespace Rodaje.App.Views.Collab;

/// <summary>Los doce colores de las personas de la sala, legibles sobre el tema oscuro y distinguibles entre sí.</summary>
internal static class CollabPalette
{
    private static readonly Color[] Colors =
    [
        Color.FromArgb(255, 0x4F, 0xC3, 0xF7), Color.FromArgb(255, 0x81, 0xC7, 0x84), Color.FromArgb(255, 0xFF, 0xB7, 0x4D),
        Color.FromArgb(255, 0xBA, 0x68, 0xC8), Color.FromArgb(255, 0xF0, 0x62, 0x92), Color.FromArgb(255, 0x4D, 0xB6, 0xAC),
        Color.FromArgb(255, 0xFF, 0xD5, 0x4F), Color.FromArgb(255, 0x79, 0x86, 0xCB), Color.FromArgb(255, 0xA1, 0x88, 0x7F),
        Color.FromArgb(255, 0x90, 0xA4, 0xAE), Color.FromArgb(255, 0xAE, 0xD5, 0x81), Color.FromArgb(255, 0xFF, 0x8A, 0x65),
    ];

    public static Color ColorOf(int index) => Colors[((index % MemberColors.Count) + MemberColors.Count) % MemberColors.Count];

    public static SolidColorBrush BrushOf(int index) => new(ColorOf(index));
}
