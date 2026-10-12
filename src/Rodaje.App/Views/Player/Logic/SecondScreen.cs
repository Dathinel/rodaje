namespace Rodaje.App.Views.Player;

/// <summary>Un rectángulo de pantalla en píxeles físicos del escritorio.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < X + Width && y < Y + Height;

    public long Area => (long)Width * Height;
}

/// <summary>A qué pantalla va el «Visor en otra pantalla».</summary>
public static class SecondScreen
{
    /// <summary>
    /// La pantalla más grande que no tiene el centro de la ventana del editor; -1 si solo hay una (o la ventana las cubre todas).
    /// </summary>
    public static int Pick(IReadOnlyList<ScreenRect> screens, ScreenRect editorWindow)
    {
        ArgumentNullException.ThrowIfNull(screens);
        int centerX = editorWindow.X + editorWindow.Width / 2;
        int centerY = editorWindow.Y + editorWindow.Height / 2;
        int best = -1;
        for (int i = 0; i < screens.Count; i++)
        {
            if (!screens[i].Contains(centerX, centerY) && (best < 0 || screens[i].Area > screens[best].Area))
            {
                best = i;
            }
        }

        return best;
    }
}
