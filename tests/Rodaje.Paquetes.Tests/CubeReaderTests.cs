using System.Diagnostics;
using System.Globalization;
using System.Text;
using Rodaje.Paquetes.Content;

namespace Rodaje.Paquetes.Tests;

[Collection(Fuzz.TimingGroup.Name)]
public sealed class CubeReaderTests
{
    internal static string Identity(int size, string header = "")
    {
        var text = new StringBuilder(header);
        text.Append(CultureInfo.InvariantCulture, $"LUT_3D_SIZE {size}\n");
        for (int b = 0; b < size; b++)
        {
            for (int g = 0; g < size; g++)
            {
                for (int r = 0; r < size; r++)
                {
                    text.Append(CultureInfo.InvariantCulture, $"{r / (double)(size - 1):0.000000} {g / (double)(size - 1):0.000000} {b / (double)(size - 1):0.000000}\n");
                }
            }
        }

        return text.ToString();
    }

    private static LutTable Read(string text) => ContentReaders.ReadCube(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    private static string Error(string text) => Assert.Throws<PackageContentException>(() => Read(text)).Message;

    [Fact]
    public void Lee_una_identidad_con_titulo_comentarios_y_orden_r_g_b()
    {
        LutTable lut = Read(Identity(3, "# hecho a mano\nTITLE \"Prueba\"\n\n"));
        Assert.Equal("Prueba", lut.Title);
        Assert.Equal(3, lut.Size);
        Assert.Equal(81, lut.Rgb.Length);
        Assert.Equal([0.5f, 0, 0], lut.Rgb[3..6]);
        Assert.Equal([0, 0.5f, 0], lut.Rgb[9..12]);
        Assert.Equal([1f, 1f, 1f], lut.Rgb[^3..]);
    }

    [Fact]
    public void Acepta_bom_crlf_tabuladores_y_notacion_cientifica()
    {
        string text = "﻿LUT_3D_SIZE 2\r\n" + string.Concat(Enumerable.Repeat("1e-1\t+2.5E-1  .5 \r\n", 8));
        LutTable lut = Read(text);
        Assert.Equal(0.1f, lut.Rgb[0], 6);
        Assert.Equal(0.25f, lut.Rgb[1], 6);
        Assert.Equal(0.5f, lut.Rgb[2], 6);
    }

    [Fact]
    public void Sin_titulo_queda_vacio_y_el_titulo_se_limpia()
    {
        Assert.Equal("", Read(Identity(2)).Title);
        Assert.Equal("ab", Read(Identity(2, "TITLE \"a‮b\"\n")).Title);
    }

    [Fact]
    public void Lleva_el_dominio_a_cero_uno()
    {
        LutTable lut = Read("DOMAIN_MIN 0 0 0\nDOMAIN_MAX 2 4 1\nLUT_3D_SIZE 2\n" + string.Concat(Enumerable.Repeat("2 2 0.5\n", 8)));
        Assert.Equal([1f, 0.5f, 0.5f], lut.Rgb[..3]);
    }

    [Fact]
    public void Acepta_LUT_3D_INPUT_RANGE()
    {
        LutTable lut = Read("LUT_3D_INPUT_RANGE 0 10\nLUT_3D_SIZE 2\n" + string.Concat(Enumerable.Repeat("5 10 0\n", 8)));
        Assert.Equal([0.5f, 1f, 0f], lut.Rgb[..3]);
    }

    [Theory]
    [InlineData("LUT_1D_SIZE 1024\n0 0 0\n", "LUT 1D")]
    [InlineData("LUT_1D_INPUT_RANGE 0 1\n", "LUT 1D")]
    [InlineData("LUT_3D_SIZE 1\n0 0 0\n", "de 2 a 65")]
    [InlineData("LUT_3D_SIZE 66\n", "de 2 a 65")]
    [InlineData("LUT_3D_SIZE dos\n", "de 2 a 65")]
    [InlineData("LUT_3D_SIZE 2\nLUT_3D_SIZE 2\n", "LUT_3D_SIZE está repetido")]
    [InlineData("TITLE \"a\"\nTITLE \"b\"\n", "TITLE está repetido")]
    [InlineData("DOMAIN_MIN 0 0 0\nDOMAIN_MIN 0 0 0\n", "DOMAIN_MIN está repetido")]
    [InlineData("DOMAIN_MAX 1 1\n", "DOMAIN_MAX necesita tres números")]
    [InlineData("DOMAIN_MIN 0 0 0\nLUT_3D_INPUT_RANGE 0 1\n", "dominio está repetido")]
    [InlineData("LUT_3D_INPUT_RANGE 0\n", "necesita dos números")]
    [InlineData("LUT_3D_SIZE 2\nCOLOR_SPACE rec709\n", "palabra clave desconocida «COLOR_SPACE»")]
    [InlineData("0 0 0\n", "falta LUT_3D_SIZE")]
    [InlineData("", "Falta LUT_3D_SIZE")]
    [InlineData("# solo un comentario\n", "Falta LUT_3D_SIZE")]
    [InlineData("LUT_3D_SIZE 2\n0 0 0\n", "trae 1 entradas y LUT_3D_SIZE 2 pide 8")]
    [InlineData("LUT_3D_SIZE 2\n0 0\n", "Línea 2 del .cube: se esperaban tres números")]
    [InlineData("LUT_3D_SIZE 2\n0 0 0 0\n", "se esperaban tres números")]
    [InlineData("LUT_3D_SIZE 2\n0 x 0\n", "se esperaban tres números")]
    [InlineData("LUT_3D_SIZE 2\n0 NaN 0\n", "se esperaban tres números")]
    [InlineData("LUT_3D_SIZE 2\n0 0 Infinity\n", "se esperaban tres números")]
    [InlineData("LUT_3D_SIZE 2\n1e999 0 0\n", "se esperaban tres números")]
    [InlineData("LUT_3D_SIZE 2\n0 0 0\nTITLE \"tarde\"\n", "«TITLE» tiene que ir antes de los datos")]
    public void Rechaza_con_el_motivo(string text, string expected) => Assert.Contains(expected, Error(text), StringComparison.Ordinal);

    [Fact]
    public void Rechaza_entradas_de_mas()
    {
        Assert.Contains("más de las 8 entradas", Error(Identity(2) + "0 0 0\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void Rechaza_un_dominio_invertido()
    {
        Assert.Contains("DOMAIN_MIN tiene que ser menor", Error("DOMAIN_MIN 1 0 0\nDOMAIN_MAX 1 1 1\n" + Identity(2)), StringComparison.Ordinal);
    }

    [Fact]
    public void Rechaza_mas_de_16_MB_tambien_sin_poder_buscar()
    {
        byte[] big = new byte[(int)LutTable.MaxFileBytes + 10];
        big.AsSpan().Fill((byte)'#');
        Assert.Contains("16 MB", Assert.Throws<PackageContentException>(() => ContentReaders.ReadCube(new MemoryStream(big))).Message, StringComparison.Ordinal);
        Assert.Contains("16 MB", Assert.Throws<PackageContentException>(() => ContentReaders.ReadCube(new OneWayStream(big))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lee_de_un_flujo_que_no_se_puede_buscar()
    {
        LutTable lut = ContentReaders.ReadCube(new OneWayStream(Encoding.UTF8.GetBytes(Identity(5))));
        Assert.Equal(5, lut.Size);
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void Lee_65_al_cubo_en_150_ms_o_menos()
    {
        byte[] data = Encoding.UTF8.GetBytes(Identity(65));
        ContentReaders.ReadCube(new MemoryStream(data));
        long best = long.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            LutTable lut = ContentReaders.ReadCube(new MemoryStream(data));
            best = Math.Min(best, watch.ElapsedMilliseconds);
            Assert.Equal(65 * 65 * 65 * 3, lut.Rgb.Length);
        }

        Assert.True(best <= 150, $"65³ tardó {best} ms");
    }

    [Fact]
    public void Mil_cube_aleatorios_o_dañados_no_tumban_el_lector()
    {
        var random = new Random(20261010);
        byte[] valid = Encoding.UTF8.GetBytes(Identity(4, "TITLE \"base\"\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n"));
        byte[] alphabet = "0123456789.-+eE \t\n\r#\"TITLELUT_3D_SIZEDOMAIN_MAX1Dÿ"u8.ToArray();
        int accepted = 0;
        for (int i = 0; i < 1000; i++)
        {
            byte[] data = (i % 4) switch
            {
                0 => Mutate(valid, random),
                1 => valid[..random.Next(valid.Length)],
                2 => RandomBytes(random, alphabet, random.Next(4096)),
                _ => RandomCube(random),
            };
            try
            {
                LutTable lut = ContentReaders.ReadCube(new MemoryStream(data));
                Assert.InRange(lut.Size, LutTable.MinSize, LutTable.MaxSize);
                Assert.Equal(lut.Size * lut.Size * lut.Size * 3, lut.Rgb.Length);
                Assert.All(lut.Rgb, v => Assert.True(float.IsFinite(v)));
                accepted++;
            }
            catch (PackageContentException)
            {
            }
        }

        Assert.InRange(accepted, 1, 999);
    }

    private static byte[] Mutate(byte[] source, Random random)
    {
        byte[] data = (byte[])source.Clone();
        int changes = random.Next(1, 6);
        for (int c = 0; c < changes; c++)
        {
            data[random.Next(data.Length)] = (byte)random.Next(256);
        }

        return data;
    }

    private static byte[] RandomBytes(Random random, byte[] alphabet, int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = alphabet[random.Next(alphabet.Length)];
        }

        return data;
    }

    private static byte[] RandomCube(Random random)
    {
        int size = random.Next(0, 6);
        var text = new StringBuilder("LUT_3D_SIZE " + size.ToString(CultureInfo.InvariantCulture) + "\n");
        int lines = (size * size * size) + random.Next(-1, 2);
        for (int i = 0; i < lines; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"{(random.NextDouble() * 4) - 2:R} {random.NextDouble():R} {random.Next(-5, 5)}\n");
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>Un flujo sin Length ni Seek, como uno de red o de un zip.</summary>
    private sealed class OneWayStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 7000));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
