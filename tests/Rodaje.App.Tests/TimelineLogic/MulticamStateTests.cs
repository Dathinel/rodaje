namespace Rodaje.App.Tests.TimelineLogic;

public sealed class MulticamStateTests
{
    [Fact]
    public void TimelineShowsNamedClickableAnglesAndUsesLongActionProgressForSync()
    {
        string root = RepositoryRoot();
        string timeline = File.ReadAllText(Path.Combine(root, "src", "Rodaje.App", "Views", "Timeline", "TimelineView.Compounds.cs"));
        Assert.Contains("RefreshMulticamPanel", timeline, StringComparison.Ordinal);
        Assert.Contains("SwitchAngle(angle)", timeline, StringComparison.Ordinal);
        Assert.Contains("SetName(_multicamButtons[i]", timeline, StringComparison.Ordinal);
        string sync = File.ReadAllText(Path.Combine(root, "src", "Rodaje.App", "Views", "Library", "LibraryView.Pro.cs"));
        Assert.Contains("LongActions.RunAsync(\"multicam.sync\"", sync, StringComparison.Ordinal);
        Assert.Contains("scope.Report((double)done / total", sync, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Rodaje.slnx"))) return d.FullName;
        throw new InvalidOperationException("No encuentro Rodaje.slnx.");
    }
}