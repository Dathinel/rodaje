using Rodaje.Packages;

namespace Rodaje.App.Views.Library.Logic;

internal static class LibrarySoundCommands
{
    public static bool InsertsAtCursorOnDoubleTap(ItemKind kind) => kind == ItemKind.Sound;

    public static (string Text, string Glyph) Preview(bool playing) =>
        playing ? ("Detener", "\uE71A") : ("Escuchar", "\uE768");
}
