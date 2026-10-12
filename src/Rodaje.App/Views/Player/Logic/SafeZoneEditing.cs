using Rodaje.Packages.Content;

namespace Rodaje.App.Views.Player;

/// <summary>Qué parte de una zona queda bajo el puntero en el editor de zonas propias.</summary>
public enum SafeZoneHandle
{
    None,
    Body,
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft,
}

/// <summary>
/// El editor de zonas propias del visor, sin WinUI: qué zona y qué esquina hay bajo el puntero y qué hace un arrastre.
/// Todo en fracciones de la imagen (0 a 1); las zonas nunca salen de la imagen ni quedan más chicas que <see cref="MinSize"/>.
/// </summary>
public static class SafeZoneEditing
{
    public const double MinSize = 0.02;

    // El tope de aspect en rodaje.safezones.
    private const int MaxAspect = 100;

    /// <summary>
    /// La zona bajo (<paramref name="x"/>, <paramref name="y"/>): primero las esquinas (a <paramref name="gripX"/> ×
    /// <paramref name="gripY"/> de distancia) y después el cuerpo, de la de encima (la última) a la de abajo.
    /// </summary>
    public static (int Index, SafeZoneHandle Handle) Hit(IReadOnlyList<SafeZone> zones, double x, double y, double gripX, double gripY)
    {
        ArgumentNullException.ThrowIfNull(zones);
        for (int i = zones.Count - 1; i >= 0; i--)
        {
            SafeZone z = zones[i];
            (double cx, double cy, SafeZoneHandle handle)[] corners =
            [
                (z.Left, z.Top, SafeZoneHandle.TopLeft),
                (z.Left + z.Width, z.Top, SafeZoneHandle.TopRight),
                (z.Left + z.Width, z.Top + z.Height, SafeZoneHandle.BottomRight),
                (z.Left, z.Top + z.Height, SafeZoneHandle.BottomLeft),
            ];
            foreach ((double cx, double cy, SafeZoneHandle handle) in corners)
            {
                if (Math.Abs(x - cx) <= gripX && Math.Abs(y - cy) <= gripY)
                {
                    return (i, handle);
                }
            }
        }

        for (int i = zones.Count - 1; i >= 0; i--)
        {
            SafeZone z = zones[i];
            if (x >= z.Left && x <= z.Left + z.Width && y >= z.Top && y <= z.Top + z.Height)
            {
                return (i, SafeZoneHandle.Body);
            }
        }

        return (-1, SafeZoneHandle.None);
    }

    /// <summary>
    /// La zona tras arrastrar <paramref name="handle"/> (<paramref name="dx"/>, <paramref name="dy"/>) desde como estaba al
    /// empezar: el cuerpo la mueve sin salir de la imagen; una esquina la estira con la esquina opuesta quieta.
    /// </summary>
    public static SafeZone Drag(SafeZone start, SafeZoneHandle handle, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(start);
        double left = start.Left;
        double top = start.Top;
        double right = start.Left + start.Width;
        double bottom = start.Top + start.Height;
        switch (handle)
        {
            case SafeZoneHandle.Body:
                double x = Math.Clamp(left + dx, 0, 1 - start.Width);
                double y = Math.Clamp(top + dy, 0, 1 - start.Height);
                return start with { Left = x, Top = y };
            case SafeZoneHandle.TopLeft:
                left = Math.Clamp(left + dx, 0, right - MinSize);
                top = Math.Clamp(top + dy, 0, bottom - MinSize);
                break;
            case SafeZoneHandle.TopRight:
                right = Math.Clamp(right + dx, left + MinSize, 1);
                top = Math.Clamp(top + dy, 0, bottom - MinSize);
                break;
            case SafeZoneHandle.BottomRight:
                right = Math.Clamp(right + dx, left + MinSize, 1);
                bottom = Math.Clamp(bottom + dy, top + MinSize, 1);
                break;
            case SafeZoneHandle.BottomLeft:
                left = Math.Clamp(left + dx, 0, right - MinSize);
                bottom = Math.Clamp(bottom + dy, top + MinSize, 1);
                break;
            default:
                return start;
        }

        return start with { Left = left, Top = top, Width = right - left, Height = bottom - top };
    }

    /// <summary>Una zona nueva en el centro, corrida un poco por cada una que ya hay para que no se tapen.</summary>
    public static SafeZone NewZone(int existing)
    {
        double offset = (Math.Max(0, existing) % 8) * 0.03;
        return new SafeZone("Zona", SafeZoneKind.Interface, 0.3 + offset, 0.4 + offset, 0.4, 0.12);
    }

    /// <summary>La proporción del proyecto reducida («9:16», «4:5»), o (0, 0) si no cabe en el formato (hasta 100).</summary>
    public static (int Width, int Height) Aspect(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return (0, 0);
        }

        int a = width;
        int b = height;
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        (int w, int h) = (width / a, height / a);
        return w <= MaxAspect && h <= MaxAspect ? (w, h) : (0, 0);
    }
}
