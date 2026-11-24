namespace Rodaje.App.Shell;

internal static class SidePanelLayout
{
    internal static (double Library, double Inspector) Fit(double width, double library, double inspector)
    {
        library = width < 544 ? 0 : Math.Max(220, library);
        inspector = width < 788 ? 0 : Math.Max(240, inspector);
        double room = width - 320 - (library > 0 ? 4 : 0) - (inspector > 0 ? 4 : 0);
        double excess = Math.Max(0, library + inspector - room);
        double cut = Math.Min(excess, Math.Max(0, library - 220));
        library -= cut;
        excess -= cut;
        if (inspector > 0)
        {
            inspector = Math.Max(240, inspector - excess);
        }

        return (library, inspector);
    }
}
