using Rodaje.Playback;

namespace Rodaje.Audio;

public static class AudioOutput
{
    /// <summary>Abre el dispositivo de salida predeterminado; si no hay ninguno usable, devuelve una salida muda con el reloj del sistema.</summary>
    /// <remarks>Mirar <see cref="IAudioOutput.HasDevice"/> y <see cref="IAudioOutput.FallbackReason"/> para avisar al usuario.</remarks>
    public static IAudioOutput Open(IAudioSampleSource source, AudioOutputOptions? options = null) =>
        Open(source, options ?? new AudioOutputOptions(), WasapiOutput.Open);

    internal static IAudioOutput Open(
        IAudioSampleSource source,
        AudioOutputOptions options,
        Func<IAudioSampleSource, AudioOutputOptions, IAudioOutput> openDevice)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        try
        {
            return openDevice(source, options);
        }
        catch (AudioDeviceUnavailableException ex)
        {
            return new NullAudioOutput(ex.Message, options.Volume);
        }
    }
}

/// <summary>Salida sin dispositivo: no suena y el reloj es el del sistema, para que la reproducción siga.</summary>
/// <remarks>No lee la fuente: sin dispositivo nadie consume el audio.</remarks>
public sealed class NullAudioOutput : IAudioOutput
{
    private float _volume;

    public NullAudioOutput(string reason, float volume = 1f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        FallbackReason = reason;
        Volume = volume;
    }

    public IAudioClock Clock { get; } = new SystemClock();

    public bool HasDevice => false;

    public string FallbackReason { get; }

    public AudioDeviceInfo? Device => null;

    public float Volume
    {
        get => Volatile.Read(ref _volume);
        set => Volatile.Write(ref _volume, float.IsNaN(value) ? throw new ArgumentOutOfRangeException(nameof(value)) : Math.Clamp(value, 0f, 1f));
    }

    public AudioOutputStats Stats => default;

    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged
    {
        add
        {
        }

        remove
        {
        }
    }

    public void Dispose()
    {
    }
}
