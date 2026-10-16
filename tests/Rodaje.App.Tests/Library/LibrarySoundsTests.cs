using Rodaje.App.Views.Library.Logic;
using Rodaje.Packages;

namespace Rodaje.App.Tests.Library;

public class LibrarySoundsTests
{
    [Fact]
    public void ElDobleClicEnSonidoLoAnadeEnElCursor()
    {
        Assert.True(LibrarySoundCommands.InsertsAtCursorOnDoubleTap(ItemKind.Sound));
        Assert.False(LibrarySoundCommands.InsertsAtCursorOnDoubleTap(ItemKind.Transition));
    }

    [Theory]
    [InlineData(false, "Escuchar", "\uE768")]
    [InlineData(true, "Detener", "\uE71A")]
    public void LaPreescuchaMuestraElComandoCorrespondiente(bool playing, string text, string glyph)
    {
        Assert.Equal((text, glyph), LibrarySoundCommands.Preview(playing));
    }
}
