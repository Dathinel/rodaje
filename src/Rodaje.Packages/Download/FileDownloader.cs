using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Rodaje.Packages.Download;

/// <summary>Un archivo que se va a descargar: de dónde, su SHA-256 (hexadecimal en minúsculas) y su tamaño exacto.</summary>
public sealed record DownloadTarget(Uri Url, string Sha256, long Size);

/// <summary>Bytes recibidos (contando lo que ya estaba de una descarga anterior) sobre el total.</summary>
public readonly record struct DownloadProgress(long Received, long Total)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp(Received / (double)Total, 0, 1);
}

/// <summary>
/// Descarga un archivo verificando su SHA-256 al vuelo. Lo recibido va a <c>&lt;destino&gt;.parcial</c>, que sobrevive a
/// un corte de red, a una cancelación o a un apagón; la siguiente vez se reanuda con <c>Range</c> desde donde quedó. Solo
/// cuando el tamaño y el hash coinciden se vacía al disco y se renombra al destino, así el destino nunca existe a medias.
/// Si no coinciden, el archivo se borra; si la parte previa venía dañada (un apagón puede dejar basura al final), se
/// intenta una vez más desde cero.
/// </summary>
public sealed class FileDownloader(HttpClient http)
{
    public const string PartialSuffix = ".parcial";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>Reintentos tras un corte de red, reanudando cada vez.</summary>
    public int MaxAttempts { get; init; } = 5;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Sin recibir nada durante este tiempo se da la conexión por caída y se reintenta.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Bytes pedidos en total por las últimas descargas (para las pruebas de reanudación).</summary>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    private long _bytesReceived;

    /// <summary>Descarga <paramref name="target"/> a <paramref name="destination"/> o confirma que ya está.</summary>
    /// <exception cref="DownloadVerificationException">Lo descargado no coincide con el hash o el tamaño; se borró.</exception>
    /// <exception cref="PackageDownloadException">El servidor no lo da o la red no volvió tras los reintentos (lo recibido queda para reanudar).</exception>
    /// <exception cref="OperationCanceledException">Se canceló; lo recibido queda para reanudar.</exception>
    public async Task DownloadAsync(DownloadTarget target, string destination, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        if (!SignedIndex.IsSha256(target.Sha256) || target.Size <= 0)
        {
            throw new ArgumentException("Falta el SHA-256 o el tamaño del archivo.", nameof(target));
        }

        destination = Path.GetFullPath(destination);
        if (File.Exists(destination))
        {
            if (new FileInfo(destination).Length == target.Size && await HashFileAsync(destination, cancellationToken) == target.Sha256)
            {
                progress?.Report(new DownloadProgress(target.Size, target.Size));
                return;
            }

            File.Delete(destination);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string partial = destination + PartialSuffix;
        bool restartedFromZero = false;
        int failures = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (offset > target.Size)
            {
                File.Delete(partial);
                offset = 0;
            }

            string? actual;
            try
            {
                actual = await ReceiveAsync(target, partial, offset, progress, cancellationToken);
            }
            catch (NetworkCutException ex)
            {
                if (++failures >= MaxAttempts)
                {
                    throw new PackageDownloadException("Se cortó la conexión y no volvió; lo descargado se guarda para seguir más tarde. " + ex.Message, ex.InnerException ?? ex);
                }

                await Task.Delay(RetryDelay, cancellationToken);
                continue;
            }

            if (actual is null)
            {
                if (++failures >= MaxAttempts)
                {
                    throw new PackageDownloadException("El servidor no entrega el archivo completo.");
                }

                continue;
            }

            if (actual == target.Sha256)
            {
                using (var stream = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    stream.Flush(flushToDisk: true);
                }

                File.Move(partial, destination, overwrite: true);
                return;
            }

            TryDelete(partial);
            if (offset > 0 && !restartedFromZero)
            {
                restartedFromZero = true;
                continue;
            }

            throw new DownloadVerificationException($"El archivo descargado no es el publicado (su SHA-256 es {actual}); se borró.");
        }
    }

    /// <summary>
    /// Un intento: pide desde <paramref name="offset"/> y agrega al parcial hasta completar el tamaño, calculando el hash
    /// al vuelo (lo que ya estaba se lee una vez). Devuelve el SHA-256 de todo, o <c>null</c> si hay que volver a empezar.
    /// </summary>
    private async Task<string?> ReceiveAsync(DownloadTarget target, string partial, long offset, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (offset == target.Size)
        {
            return await HashFileAsync(partial, cancellationToken);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new NetworkCutException(ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NetworkCutException(ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                TryDelete(partial);
                return null;
            }

            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new NetworkCutException(new HttpRequestException($"El servidor respondió {(int)response.StatusCode}."));
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new PackageDownloadException($"El servidor no tiene el archivo ({(int)response.StatusCode} {response.ReasonPhrase}): {target.Url}");
            }

            bool append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == offset;
            if (offset > 0 && !append)
            {
                // El servidor ignoró el Range o respondió otro tramo: se empieza de cero con esta misma respuesta.
                offset = 0;
            }

            if (response.Content.Headers.ContentLength is long length && offset + length > target.Size)
            {
                TryDelete(partial);
                throw new DownloadVerificationException("El servidor ofrece un archivo más grande que el publicado; no se descargó.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (append)
            {
                await HashPrefixAsync(partial, hash, cancellationToken);
            }

            using var file = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            Stream body;
            try
            {
                body = await response.Content.ReadAsStreamAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new NetworkCutException(ex);
            }

            await using (body)
            {
                byte[] buffer = new byte[1 << 16];
                long received = offset;
                progress?.Report(new DownloadProgress(received, target.Size));
                while (true)
                {
                    int read;
                    using (var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        stall.CancelAfter(StallTimeout);
                        try
                        {
                            read = await body.ReadAsync(buffer, stall.Token);
                        }
                        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new NetworkCutException(ex);
                        }
                        catch (Exception ex) when (ex is HttpRequestException or IOException)
                        {
                            throw new NetworkCutException(ex);
                        }
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    Interlocked.Add(ref _bytesReceived, read);
                    if (received + read > target.Size)
                    {
                        file.Dispose();
                        TryDelete(partial);
                        throw new DownloadVerificationException("El servidor envió más bytes de los publicados; se borró lo descargado.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    progress?.Report(new DownloadProgress(received, target.Size));
                }

                if (received < target.Size)
                {
                    throw new NetworkCutException(new IOException($"La conexión terminó en {received} de {target.Size} bytes."));
                }

                return Convert.ToHexStringLower(hash.GetHashAndReset());
            }
        }
    }

    private static async Task HashPrefixAsync(string path, IncrementalHash hash, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);
        byte[] buffer = new byte[1 << 16];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class NetworkCutException(Exception inner) : Exception(inner.Message, inner);
}
