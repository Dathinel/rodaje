namespace Rodaje.Core.Tests.Caza;

using Rodaje.Core.Editing;

public class EditExceptionCazaTests
{
    [Fact]
    public void InheritsFromExceptionAndCanBeThrown()
    {
        void ThrowAction() => throw new EditException("Error");

        var exception = Assert.Throws<EditException>(ThrowAction);
        Assert.IsAssignableFrom<Exception>(exception);
    }

    [Fact]
    public void PreservesMessageReadyToDisplay()
    {
        const string expectedMessage = "Cannot perform edit";
        var exception = new EditException(expectedMessage);

        Assert.Equal(expectedMessage, exception.Message);
    }
}
