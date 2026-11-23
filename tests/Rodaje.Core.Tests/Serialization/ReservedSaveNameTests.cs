using Rodaje.Core.Serialization;

namespace Rodaje.Core.Tests.Serialization;

public class ReservedSaveNameTests
{
    [Theory]
    [InlineData("CON.rodaje")]
    [InlineData("conin$.rodaje")]
    [InlineData("CONOUT$.rodaje")]
    [InlineData("COM¹.rodaje")]
    public void Windows_device_names_are_refused_before_touching_the_disk(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string folder = Path.Combine(Path.GetTempPath(), "rodaje-reservado-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Throws<ArgumentException>(() => ProjectSerializer.Save(SerializationSamples.Rich(), Path.Combine(folder, name)));
            Assert.Empty(Directory.GetFileSystemEntries(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
