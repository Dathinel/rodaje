namespace Rodaje.Analysis.Video;

/// <summary>Pirámide de una imagen gris (cada nivel la mitad del anterior) con las derivadas de cada nivel.</summary>
internal sealed class Pyramid
{
    public Pyramid(int width, int height, int levels)
    {
        Levels = new GrayImage[levels];
        Dx = new float[levels][];
        Dy = new float[levels][];
        for (int l = 0; l < levels; l++)
        {
            Levels[l] = new GrayImage(Math.Max(1, width >> l), Math.Max(1, height >> l));
            Dx[l] = new float[Levels[l].Pixels.Length];
            Dy[l] = new float[Levels[l].Pixels.Length];
        }
    }

    public GrayImage[] Levels { get; }

    public float[][] Dx { get; }

    public float[][] Dy { get; }

    public GrayImage Base => Levels[0];

    /// <summary>Arma los niveles superiores y las derivadas a partir de <see cref="Base"/>, ya escrito.</summary>
    public void Build()
    {
        for (int l = 1; l < Levels.Length; l++)
        {
            GrayImage.Half(Levels[l - 1], Levels[l]);
        }

        Parallel.For(0, Levels.Length, l => GrayImage.Gradients(Levels[l], Dx[l], Dy[l]));
    }
}
