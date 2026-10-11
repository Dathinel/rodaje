# Genera las 8 LUT de los looks de rodaje-filtros en <Destino>\luts\, con fórmulas propias y deterministas:
# la misma salida byte a byte en cualquier equipo. Lo llama empaquetar.ps1.
# Cada look es una función de un color RGB de 0 a 1 que devuelve otro; la LUT la muestrea en una rejilla de 33³.
# Las fórmulas van en C# porque en PowerShell las 8 × 33³ llamadas tardan minutos.
param([Parameter(Mandatory = $true)][string]$Destino)

$ErrorActionPreference = 'Stop'

if (-not ('RodajeFiltros.Looks' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace RodajeFiltros
{
    public static class Looks
    {
        public const int Size = 33;

        public static readonly string[] Names =
        {
            "calido", "frio", "verde-azulado-naranja", "blanco-y-negro", "desvaido", "vivo", "noche", "cine",
        };

        public static void Apply(string name, double r, double g, double b, double[] o)
        {
            switch (name)
            {
                case "calido":
                    r = S(r, 0.15); g = S(g, 0.15); b = S(b, 0.15);
                    Set(o, r * 1.06 + 0.02, g * 1.01 + 0.005, b * 0.88);
                    break;
                case "frio":
                    r = S(r, 0.1); g = S(g, 0.1); b = S(b, 0.1);
                    Set(o, r * 0.9, g * 0.99 + 0.01, b * 1.06 + 0.03);
                    break;
                case "verde-azulado-naranja":
                {
                    // Sombras hacia verde azulado y luces hacia naranja, según la luminancia.
                    Saturate(S(r, 0.3), S(g, 0.3), S(b, 0.3), 1.1, o);
                    double t = Smooth(Luma(o[0], o[1], o[2]));
                    Set(o, o[0] + 0.12 * Lerp(-0.9, 0.6, t), o[1] + 0.12 * Lerp(0.25, 0.1, t), o[2] + 0.12 * Lerp(0.6, -0.7, t));
                    break;
                }
                case "blanco-y-negro":
                {
                    double l = S(Luma(r, g, b), 0.3);
                    Set(o, l, l, l);
                    break;
                }
                case "desvaido":
                    // Negros levantados, blancos bajados y un toque cálido.
                    Saturate(r, g, b, 0.7, o);
                    Set(o, 0.09 + o[0] * 0.83, 0.08 + o[1] * 0.82, 0.07 + o[2] * 0.8);
                    break;
                case "vivo":
                    Saturate(S(r, 0.25), S(g, 0.25), S(b, 0.25), 1.35, o);
                    break;
                case "noche":
                    // Noche americana: más oscuro, poco saturado y azul.
                    Saturate(r, g, b, 0.55, o);
                    Set(o, Math.Pow(Clamp(o[0] * 0.7), 1.25), Math.Pow(Clamp(o[1] * 0.82), 1.2), Math.Pow(Clamp(o[2] * 1.05 + 0.02), 1.1));
                    break;
                case "cine":
                {
                    Saturate(S(r, 0.35), S(g, 0.35), S(b, 0.35), 0.9, o);
                    double t = Smooth(Luma(o[0], o[1], o[2]));
                    Set(o, 0.02 + o[0] * 0.97 + 0.03 * t, 0.025 + o[1] * 0.96 + 0.01 * t, 0.03 + o[2] * 0.95 - 0.03 * t);
                    break;
                }
                default:
                    throw new ArgumentException("Look desconocido: " + name);
            }
        }

        public static string Cube(string name)
        {
            var text = new StringBuilder(1200000);
            text.Append("# rodaje-filtros: ").Append(name).Append(". Generada por generar.ps1 de rodaje-filtros (CC0-1.0).\n");
            text.Append("TITLE \"").Append(name).Append("\"\nLUT_3D_SIZE ").Append(Size).Append("\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n");
            var o = new double[3];
            for (int b = 0; b < Size; b++)
            {
                for (int g = 0; g < Size; g++)
                {
                    for (int r = 0; r < Size; r++)
                    {
                        Apply(name, r / (double)(Size - 1), g / (double)(Size - 1), b / (double)(Size - 1), o);
                        text.Append(F(o[0])).Append(' ').Append(F(o[1])).Append(' ').Append(F(o[2])).Append('\n');
                    }
                }
            }

            return text.ToString();
        }

        public static void Write(string folder)
        {
            Directory.CreateDirectory(folder);
            foreach (string name in Names)
            {
                File.WriteAllText(Path.Combine(folder, name + ".cube"), Cube(name), new UTF8Encoding(false));
            }
        }

        private static string F(double x) { return Clamp(x).ToString("0.000000", CultureInfo.InvariantCulture); }

        private static double Clamp(double x) { return x < 0 ? 0 : x > 1 ? 1 : x; }

        private static double Lerp(double a, double b, double t) { return a + (b - a) * t; }

        private static double Smooth(double x) { x = Clamp(x); return x * x * (3 - 2 * x); }

        // Curva en S: mezcla el valor con su smoothstep; k = 0 lo deja igual.
        private static double S(double x, double k) { return Lerp(x, Smooth(x), k); }

        private static double Luma(double r, double g, double b) { return 0.2126 * r + 0.7152 * g + 0.0722 * b; }

        private static void Saturate(double r, double g, double b, double s, double[] o)
        {
            double l = Luma(r, g, b);
            Set(o, l + (r - l) * s, l + (g - l) * s, l + (b - l) * s);
        }

        private static void Set(double[] o, double r, double g, double b) { o[0] = r; o[1] = g; o[2] = b; }
    }
}
'@
}

[RodajeFiltros.Looks]::Write((Join-Path ([System.IO.Path]::GetFullPath($Destino)) 'luts'))
