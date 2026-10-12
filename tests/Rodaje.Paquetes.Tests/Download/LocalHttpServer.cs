using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Rodaje.Paquetes.Tests.Download;

/// <summary>
/// Servidor HTTP/1.1 mínimo en 127.0.0.1 para probar la descarga con sockets reales: sirve archivos en memoria, entiende
/// <c>Range: bytes=N-</c>, redirige y puede cortar la conexión a mitad de un archivo (como una red que se cae).
/// </summary>
internal sealed class LocalHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, byte[]> _files = new();
    private readonly ConcurrentDictionary<string, string> _redirects = new();

    public LocalHttpServer()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public Uri BaseUrl => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

    public ConcurrentQueue<(string Path, string? Range)> Requests { get; } = new();

    public bool SupportsRange { get; set; } = true;

    /// <summary>Las próximas respuestas de archivo se cortan tras enviar estos bytes del cuerpo (una por elemento).</summary>
    public ConcurrentQueue<long> CutAfter { get; } = new();

    /// <summary>Las próximas respuestas devuelven este código sin cuerpo.</summary>
    public ConcurrentQueue<int> FailWith { get; } = new();

    public Uri Put(string path, byte[] content)
    {
        _files[path] = content;
        return new Uri(BaseUrl, path);
    }

    public Uri Redirect(string from, string to)
    {
        _redirects[from] = to;
        return new Uri(BaseUrl, from);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                while (true)
                {
                    string? requestLine = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(requestLine))
                    {
                        return;
                    }

                    string? range = null;
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    {
                        if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                        {
                            range = line["Range:".Length..].Trim();
                        }
                    }

                    string path = requestLine.Split(' ')[1].TrimStart('/');
                    Requests.Enqueue((path, range));
                    if (!await RespondAsync(stream, path, range))
                    {
                        return;
                    }
                }
            }
            catch (IOException)
            {
            }
        }
    }

    /// <returns>Si la conexión puede seguir abierta.</returns>
    private async Task<bool> RespondAsync(NetworkStream stream, string path, string? range)
    {
        if (FailWith.TryDequeue(out int code))
        {
            await WriteHeadAsync(stream, $"{code} Error", "Content-Length: 0");
            return true;
        }

        if (_redirects.TryGetValue(path, out string? to))
        {
            await WriteHeadAsync(stream, "302 Found", $"Location: {new Uri(BaseUrl, to)}", "Content-Length: 0");
            return true;
        }

        if (!_files.TryGetValue(path, out byte[]? content))
        {
            await WriteHeadAsync(stream, "404 Not Found", "Content-Length: 0");
            return true;
        }

        long start = 0;
        if (SupportsRange && range is not null && range.StartsWith("bytes=", StringComparison.Ordinal) && range.EndsWith('-'))
        {
            start = long.Parse(range[6..^1], System.Globalization.CultureInfo.InvariantCulture);
            if (start >= content.Length)
            {
                await WriteHeadAsync(stream, "416 Range Not Satisfiable", $"Content-Range: bytes */{content.Length}", "Content-Length: 0");
                return true;
            }

            await WriteHeadAsync(stream, "206 Partial Content", $"Content-Range: bytes {start}-{content.Length - 1}/{content.Length}", $"Content-Length: {content.Length - start}");
        }
        else
        {
            await WriteHeadAsync(stream, "200 OK", $"Content-Length: {content.Length}", "Accept-Ranges: bytes");
        }

        long length = content.Length - start;
        if (CutAfter.TryDequeue(out long cut) && cut < length)
        {
            await stream.WriteAsync(content.AsMemory((int)start, (int)cut));
            await stream.FlushAsync();
            return false;
        }

        await stream.WriteAsync(content.AsMemory((int)start, (int)length));
        return true;
    }

    private static async Task WriteHeadAsync(NetworkStream stream, string status, params string[] headers)
    {
        var head = new StringBuilder($"HTTP/1.1 {status}\r\nContent-Type: application/octet-stream\r\n");
        foreach (string header in headers)
        {
            head.Append(header).Append("\r\n");
        }

        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
