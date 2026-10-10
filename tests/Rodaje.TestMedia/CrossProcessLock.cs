using System.Security.Cryptography;
using System.Text;

namespace Rodaje.TestMedia;

/// <summary>Candado con nombre, entre todos los procesos de la sesión, para generar un archivo de la caché una sola vez.</summary>
/// <remarks>Se libera en el mismo hilo que lo tomó. Si el proceso que lo tenía murió, el siguiente lo hereda.</remarks>
public sealed class CrossProcessLock : IDisposable
{
    private readonly Mutex _mutex;
    private bool _released;

    private CrossProcessLock(Mutex mutex) => _mutex = mutex;

    public static CrossProcessLock Acquire(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
        var mutex = new Mutex(initiallyOwned: false, @"Local\Rodaje-cache-" + Convert.ToHexString(hash, 0, 16));
        try
        {
            mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // El dueño anterior murió a mitad de camino: lo que dejó es un temporal con nombre único, no el archivo final.
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        return new CrossProcessLock(mutex);
    }

    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
