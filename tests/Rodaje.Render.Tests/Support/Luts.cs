using Rodaje.Playback.Composition;

namespace Rodaje.Render.Tests.Support;

/// <summary>LUT sintéticas mientras no hay lector de <c>.cube</c>: R más rápido, luego G, luego B.</summary>
internal static class Luts
{
    public static LutData Identity(int size) => Create(size, (r, g, b) => (r, g, b));

    public static LutData Create(int size, Func<double, double, double, (double R, double G, double B)> map)
    {
        var rgb = new float[size * size * size * 3];
        int i = 0;
        for (int b = 0; b < size; b++)
        {
            for (int g = 0; g < size; g++)
            {
                for (int r = 0; r < size; r++)
                {
                    (double R, double G, double B) value = map(r / (size - 1.0), g / (size - 1.0), b / (size - 1.0));
                    rgb[i++] = (float)value.R;
                    rgb[i++] = (float)value.G;
                    rgb[i++] = (float)value.B;
                }
            }
        }

        return new LutData(size, rgb);
    }
}
