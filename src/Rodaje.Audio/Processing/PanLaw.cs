namespace Rodaje.Audio.Processing;

/// <summary>Cómo sale cada canal: izquierda = L·<see cref="LeftToLeft"/> + R·<see cref="RightToLeft"/>, y lo mismo a la derecha.</summary>
public readonly record struct PanMatrix(float LeftToLeft, float RightToLeft, float LeftToRight, float RightToRight)
{
    public static PanMatrix Identity { get; } = new(1, 0, 0, 1);

    public bool IsIdentity => this == Identity;

    /// <summary>Interpola entre dos matrices (<paramref name="t"/> de 0 a 1).</summary>
    public static PanMatrix Lerp(PanMatrix a, PanMatrix b, float t) => new(
        a.LeftToLeft + ((b.LeftToLeft - a.LeftToLeft) * t),
        a.RightToLeft + ((b.RightToLeft - a.RightToLeft) * t),
        a.LeftToRight + ((b.LeftToRight - a.LeftToRight) * t),
        a.RightToRight + ((b.RightToRight - a.RightToRight) * t));
}

/// <summary>
/// Paneo con ley de −3 dB y balance. En el centro los dos dejan la señal tal cual.
/// </summary>
/// <remarks>
/// <para>
/// <b>Paneo.</b> Una fuente mono (L = R = x) sale con <c>√2·cos θ</c> a la izquierda y <c>√2·sin θ</c> a la derecha,
/// θ = (paneo + 1)·π/4: potencia constante, el centro queda en 0 dB y cada lado 3 dB por debajo de un paneo completo. En
/// estéreo se mueve la imagen: al ir a la izquierda, el canal derecho pasa poco a poco a la izquierda.
/// </para>
/// <para><b>Balance.</b> Solo baja el canal contrario, en línea recta hasta el silencio.</para>
/// </remarks>
public static class PanLaw
{
    private static readonly double Root2 = Math.Sqrt(2);

    /// <param name="pan">−1 izquierda, 0 centro, 1 derecha.</param>
    /// <param name="balance">−1 solo la izquierda, 0 los dos, 1 solo la derecha.</param>
    public static PanMatrix Matrix(double pan, double balance)
    {
        pan = Math.Clamp(double.IsNaN(pan) ? 0 : pan, -1, 1);
        balance = Math.Clamp(double.IsNaN(balance) ? 0 : balance, -1, 1);
        if (pan == 0 && balance == 0)
        {
            return PanMatrix.Identity;
        }

        double theta = (pan + 1) * Math.PI / 4;
        double left = Root2 * Math.Cos(theta);
        double right = Root2 * Math.Sin(theta);
        double ll, rl, lr, rr;
        if (pan == 0)
        {
            (ll, rl, lr, rr) = (1, 0, 0, 1);
        }
        else if (pan < 0)
        {
            (ll, rl, lr, rr) = (1, left - 1, 0, right);
        }
        else
        {
            (ll, rl, lr, rr) = (left, 0, right - 1, 1);
        }

        double keepLeft = Math.Min(1, 1 - balance);
        double keepRight = Math.Min(1, 1 + balance);
        return new PanMatrix((float)(ll * keepLeft), (float)(rl * keepLeft), (float)(lr * keepRight), (float)(rr * keepRight));
    }

    /// <summary>Ganancias de una fuente mono (izquierda, derecha): la ley de −3 dB.</summary>
    public static (double Left, double Right) MonoGains(double pan)
    {
        PanMatrix m = Matrix(pan, 0);
        return (m.LeftToLeft + m.RightToLeft, m.LeftToRight + m.RightToRight);
    }
}
