using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rodaje.App.Services.Collab;

/// <summary>Por qué un texto pegado no sirve como invitación.</summary>
public enum InviteProblem
{
    None,
    Empty,
    NotAnInvite,
    Damaged,
    OtherApp,
    Expired,
}

/// <summary>Una invitación leída: el billete que entiende Sala y cuándo caduca (si lo dice).</summary>
public sealed record InviteInfo(string Ticket, DateTimeOffset? Expires);

/// <summary>El enlace <c>rodaje://unirse/&lt;billete&gt;</c> de Sala y su lectura a prueba de lo que la gente pega.</summary>
public static partial class CollabInvite
{
    public const string Scheme = "rodaje://unirse/";
    private const string TicketPrefix = "dathinel1.";
    private const int MaxLength = 4096;

    [GeneratedRegex(@"dathinel1\.[A-Za-z0-9_\-]+")]
    private static partial Regex TicketPattern();

    /// <summary>El enlace que se manda por chat.</summary>
    public static string ToLink(string ticket) => Scheme + ticket;

    /// <summary>
    /// Saca el billete de lo que la persona pegó: el enlace, el billete suelto o un mensaje que lo trae dentro, con espacios o
    /// saltos de línea de más. No se fía de nada: solo lee el JSON del billete para avisar antes de intentarlo; el anfitrión
    /// es quien decide de verdad.
    /// </summary>
    public static InviteProblem TryParse(string? text, DateTimeOffset now, out InviteInfo? invite)
    {
        invite = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return InviteProblem.Empty;
        }

        if (text.Length > MaxLength * 4)
        {
            return InviteProblem.NotAnInvite;
        }

        // Primero tal cual (un mensaje con palabras alrededor); si no sale, con los trozos juntos (los chats parten las líneas largas).
        InviteProblem problem = Read(text, now, out invite);
        if (problem is InviteProblem.NotAnInvite or InviteProblem.Damaged)
        {
            string compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c is not ('\u200B' or '\u00AD' or '\uFEFF')));
            InviteProblem joined = Read(compact, now, out InviteInfo? second);
            if (joined is not (InviteProblem.NotAnInvite or InviteProblem.Damaged) || problem == InviteProblem.NotAnInvite)
            {
                problem = joined;
                invite = second;
            }
        }

        return problem;
    }

    private static InviteProblem Read(string text, DateTimeOffset now, out InviteInfo? invite)
    {
        invite = null;
        text = text.Replace("\u200B", "", StringComparison.Ordinal).Replace("\uFEFF", "", StringComparison.Ordinal).Replace("\u00AD", "", StringComparison.Ordinal);
        Match m = TicketPattern().Match(text);
        if (!m.Success)
        {
            return InviteProblem.NotAnInvite;
        }

        string ticket = m.Value;
        if (ticket.Length > MaxLength)
        {
            return InviteProblem.Damaged;
        }

        DateTimeOffset? expires;
        try
        {
            string payload = ticket[TicketPrefix.Length..].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using JsonDocument doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("anfitrion", out JsonElement host) || host.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("app", out JsonElement app) || app.ValueKind != JsonValueKind.String)
            {
                return InviteProblem.Damaged;
            }

            if (app.GetString() != "rodaje")
            {
                return InviteProblem.OtherApp;
            }

            expires = root.TryGetProperty("caduca", out JsonElement c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out long unix) && unix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(Math.Min(unix, 253402300799L))
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return InviteProblem.Damaged;
        }

        invite = new InviteInfo(ticket, expires);
        return expires is { } e && e <= now ? InviteProblem.Expired : InviteProblem.None;
    }
}
