using Rodaje.App.Services.Modules;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Services.Translate;

/// <summary>Los textos traducidos por id de clip y los que el módulo dejó como estaban.</summary>
public sealed record TrackTranslation(IReadOnlyDictionary<string, string> Texts, IReadOnlyList<string> Untranslated);

/// <summary>
/// El diálogo con <c>modulo-traducir</c>: un solo trabajo en Trabajos (cancelable, con avance) que llama a
/// <c>traducir</c> por tandas de <see cref="SubtitleTrackTranslation.MaxPerCall"/> y junta lo que vuelve, comprobado.
/// </summary>
public sealed class ModuleSubtitleTranslator(ModuleManager modules)
{
    public const string NotInstalled = "Instala el módulo «Traducir subtítulos» (Complementos → IA) para traducir pistas.";

    public bool IsInstalled => modules.Find(SubtitleTrackTranslation.ModuleId) is not null;

    /// <exception cref="TranslationException">Sin el módulo o sin su modelo, o el módulo devolvió algo inválido.</exception>
    /// <exception cref="ModuleException">El módulo falló o se cerró (el mensaje ya viene explicado).</exception>
    public Task<TrackTranslation> TranslateAsync(IReadOnlyList<SubtitleText> texts, string? source, string target, string title, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (modules.Find(SubtitleTrackTranslation.ModuleId) is not { } module)
        {
            throw new TranslationException(NotInstalled);
        }

        if (TranslatorModel.IsMissing(module))
        {
            throw new TranslationException(TranslatorModel.Missing);
        }

        IReadOnlyList<System.Text.Json.JsonElement> requests = SubtitleTrackTranslation.Requests(texts, source, target);
        return modules.RunAsync(SubtitleTrackTranslation.ModuleId, title, async call =>
        {
            Dictionary<string, string> all = new(StringComparer.Ordinal);
            List<string> untranslated = [];
            for (int i = 0; i < requests.Count; i++)
            {
                string[] ids = [.. texts.Skip(i * SubtitleTrackTranslation.MaxPerCall).Take(SubtitleTrackTranslation.MaxPerCall).Select(t => t.ClipId)];
                ModuleResult result = await call.InvokeAsync(SubtitleTrackTranslation.Function, requests[i], null,
                    (double)i / requests.Count, (double)(i + 1) / requests.Count).ConfigureAwait(false);
                Dictionary<string, string> parsed = SubtitleTrackTranslation.ParseResult(result.Value, ids, out IReadOnlyList<string> missing);
                foreach ((string id, string text) in parsed)
                {
                    all[id] = text;
                }

                untranslated.AddRange(missing);
            }

            return new TrackTranslation(all, untranslated);
        }, cancellationToken);
    }
}
