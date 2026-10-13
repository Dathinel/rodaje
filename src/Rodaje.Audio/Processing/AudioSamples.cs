namespace Rodaje.Audio.Processing;

/// <summary>Lo que entra en un proceso con estado (filtros, envolventes, líneas de retardo) tiene que ser un número.</summary>
public static class AudioSamples
{
    /// <summary>
    /// Lo más que vale una muestra al entrar: +60 dBFS. Ningún origen real llega ahí, y con las ganancias máximas de las
    /// inserciones (+18, +24, +36 dB) nada se desborda a infinito.
    /// </summary>
    public const float MaxMagnitude = 1000;

    /// <summary>NaN y ±∞ pasan a silencio y lo enorme se acota a <see cref="MaxMagnitude"/>; lo demás queda igual.</summary>
    public static float Sanitize(float sample) =>
        float.IsFinite(sample) ? Math.Clamp(sample, -MaxMagnitude, MaxMagnitude) : 0;

    /// <summary><see cref="Sanitize(float)"/> en el sitio; devuelve cuántas muestras cambió.</summary>
    public static int Sanitize(Span<float> samples)
    {
        int changed = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i];
            if (!float.IsFinite(sample) || MathF.Abs(sample) > MaxMagnitude)
            {
                samples[i] = Sanitize(sample);
                changed++;
            }
        }

        return changed;
    }
}
