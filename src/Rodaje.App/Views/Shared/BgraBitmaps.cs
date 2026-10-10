using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Rodaje.Media.Video;

namespace Rodaje.App.Views.Shared;

/// <summary>
/// Adaptador de <see cref="BgraImage"/> (miniaturas de <c>IClipVisuals</c>) a <see cref="WriteableBitmap"/> para un
/// <c>Image</c>. Cada imagen se convierte una vez: el mapa débil suelta el mapa de bits cuando la caché suelta la imagen.
/// Solo desde el hilo de la interfaz.
/// </summary>
internal static class BgraBitmaps
{
    private static readonly ConditionalWeakTable<BgraImage, WriteableBitmap> Bitmaps = new();

    public static WriteableBitmap From(BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (Bitmaps.TryGetValue(image, out WriteableBitmap? existing))
        {
            return existing;
        }

        var bitmap = new WriteableBitmap(image.Width, image.Height);
        image.Pixels.CopyTo(bitmap.PixelBuffer);
        bitmap.Invalidate();
        Bitmaps.AddOrUpdate(image, bitmap);
        return bitmap;
    }
}
