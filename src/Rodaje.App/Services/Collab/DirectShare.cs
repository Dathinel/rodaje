using System.Text.Json;
using Dathinel.Sala;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// Lo que comparten los repartos por mensaje directo y blob (copia ligera, original): esperar la respuesta del anfitrión con
/// señales de vida, avisar sin que un fallo de red salga como excepción, leer el diagnóstico y el texto de un rechazo.
/// </summary>
internal static class DirectShare
{
    /// <summary>
    /// Manda <paramref name="ask"/> al anfitrión y espera su respuesta. <paramref name="classify"/> dice de cada mensaje del
    /// anfitrión si es la respuesta (<c>true</c>), una señal de vida que reinicia la espera (<c>false</c>) o nada de esto
    /// (<c>null</c>). Sin respuesta ni señal durante <paramref name="timeout"/>, lanza <see cref="CollabException"/> con
    /// <paramref name="timeoutText"/>.
    /// </summary>
    public static async Task<JsonElement> AwaitReplyAsync(RoomService room, string host, JsonElement ask, Func<JsonElement, bool?> classify,
        TimeSpan timeout, string timeoutText, CancellationToken cancellationToken)
    {
        TaskCompletionSource<JsonElement> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long lastSign = Environment.TickCount64;
        void OnDirect(string from, JsonElement message)
        {
            if (from != host)
            {
                return;
            }

            switch (classify(message))
            {
                case true:
                    answer.TrySetResult(message.Clone());
                    break;
                case false:
                    Interlocked.Exchange(ref lastSign, Environment.TickCount64);
                    break;
            }
        }

        room.DirectReceived += OnDirect;
        try
        {
            await room.SendDirectAsync(ask, host, cancellationToken).ConfigureAwait(false);
            while (!answer.Task.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan left = timeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref lastSign));
                if (left <= TimeSpan.Zero)
                {
                    throw new CollabException(timeoutText);
                }

                await Task.WhenAny(answer.Task, Task.Delay(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1), cancellationToken)).ConfigureAwait(false);
            }

            return await answer.Task.ConfigureAwait(false);
        }
        finally
        {
            room.DirectReceived -= OnDirect;
        }
    }

    /// <summary>Manda un aviso; si no sale (sala cerrada, red caída), no pasa nada: quien espera verá que no llegó a tiempo.</summary>
    public static async Task TrySendAsync(RoomService room, JsonElement message, string to, CancellationToken cancellationToken)
    {
        try
        {
            await room.SendDirectAsync(message, to, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CollabException or OperationCanceledException or ObjectDisposedException)
        {
            // Es un aviso: no puede tumbar a quien lo manda.
        }
    }

    /// <summary>El diagnóstico de la sala, o <c>null</c> si no se pudo leer (entonces no se decide nada por él).</summary>
    public static async Task<Diagnostico?> ReadDiagnosticAsync(Func<CancellationToken, Task<Diagnostico?>> diagnostic, CancellationToken cancellationToken)
    {
        try
        {
            return await diagnostic(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>El texto de un rechazo del anfitrión (ajeno: sin caracteres de control y corto), o <paramref name="fallback"/>.</summary>
    public static string ErrorText(JsonElement reply, string fallback)
    {
        string text = reply.TryGetProperty("texto", out JsonElement t) && t.ValueKind == JsonValueKind.String
            ? new string([.. (t.GetString() ?? "").Where(c => !char.IsControl(c)).Take(300)])
            : "";
        return text.Length > 3 ? text : fallback;
    }

    /// <summary>Si <paramref name="path"/>, resuelta, queda dentro de <paramref name="folder"/>.</summary>
    public static bool IsInside(string folder, string path)
    {
        string full = Path.GetFullPath(path);
        return full.StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Si el mensaje trae <c>sha256</c> igual a <paramref name="hash"/>.</summary>
    public static bool IsAbout(JsonElement message, string hash) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("sha256", out JsonElement h) && h.ValueKind == JsonValueKind.String && h.GetString() == hash;
}
