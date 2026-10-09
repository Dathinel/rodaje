using FotoToma.Core.Serialization;

namespace FotoToma.Core.Tests.Serialization;

public class ProjectFormatExceptionTests
{
    [Fact]
    public void Constructors_SetMessageProblemsAndInner()
    {
        var inner = new InvalidOperationException("x");

        Assert.Equal("El archivo no es un proyecto de FotoToma válido.", new ProjectFormatException().Message);
        Assert.Empty(new ProjectFormatException().Problems);

        var withMessage = new ProjectFormatException("m");
        Assert.Equal(("m", 0), (withMessage.Message, withMessage.Problems.Count));
        Assert.Null(withMessage.InnerException);

        var withInner = new ProjectFormatException("m", inner);
        Assert.Same(inner, withInner.InnerException);
        Assert.Empty(withInner.Problems);

        var withProblems = new ProjectFormatException("m", ["a", "b"], inner);
        Assert.Equal(["a", "b"], withProblems.Problems);
        Assert.Same(inner, withProblems.InnerException);

        Assert.Throws<ArgumentNullException>(() => new ProjectFormatException("m", null!, null));
    }
}
