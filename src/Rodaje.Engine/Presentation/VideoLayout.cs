namespace Rodaje.Engine.Presentation;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public readonly record struct TexCoord(float U, float V);

/// <summary>Coordenadas de textura de las cuatro esquinas del rectángulo en pantalla.</summary>
public readonly record struct QuadTexCoords(TexCoord TopLeft, TexCoord TopRight, TexCoord BottomLeft, TexCoord BottomRight);

/// <summary>Geometría de un cuadro en pantalla: barras negras, rotación y recorte del relleno del decodificador.</summary>
public static class VideoLayout
{
    /// <summary>Rectángulo más grande con la proporción de la imagen que cabe centrado en el destino.</summary>
    /// <param name="displayWidth">Ancho en pantalla, con la relación de aspecto del píxel y la rotación ya aplicadas.</param>
    public static PixelRect Fit(int targetWidth, int targetHeight, int displayWidth, int displayHeight)
    {
        if (targetWidth <= 0 || targetHeight <= 0 || displayWidth <= 0 || displayHeight <= 0)
        {
            return default;
        }

        long widthIfFullHeight = ((long)targetHeight * displayWidth) + (displayHeight / 2);
        widthIfFullHeight /= displayHeight;
        int width;
        int height;
        if (widthIfFullHeight <= targetWidth)
        {
            width = (int)widthIfFullHeight;
            height = targetHeight;
        }
        else
        {
            width = targetWidth;
            height = (int)((((long)targetWidth * displayHeight) + (displayWidth / 2)) / displayWidth);
        }

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        return new PixelRect((targetWidth - width) / 2, (targetHeight - height) / 2, width, height);
    }

    /// <summary>Qué punto de la textura va en cada esquina de la pantalla.</summary>
    /// <param name="rotation">Rotación de pantalla en grados en sentido horario: 0, 90, 180 o 270.</param>
    /// <param name="uMax">Ancho visible entre ancho de la textura (recorta el relleno de la derecha).</param>
    /// <param name="vMax">Alto visible entre alto de la textura (recorta el relleno de abajo).</param>
    public static QuadTexCoords Corners(int rotation, float uMax, float vMax)
    {
        var tl = new TexCoord(0, 0);
        var tr = new TexCoord(uMax, 0);
        var bl = new TexCoord(0, vMax);
        var br = new TexCoord(uMax, vMax);
        // Girar la imagen en sentido horario trae a la esquina superior izquierda la que estaba abajo a la izquierda.
        return NormalizeRotation(rotation) switch
        {
            90 => new QuadTexCoords(bl, tl, br, tr),
            180 => new QuadTexCoords(br, bl, tr, tl),
            270 => new QuadTexCoords(tr, br, tl, bl),
            _ => new QuadTexCoords(tl, tr, bl, br),
        };
    }

    public static int NormalizeRotation(int rotation)
    {
        int r = ((rotation % 360) + 360) % 360;
        return (int)(Math.Round(r / 90.0) * 90) % 360;
    }
}
