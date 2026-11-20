namespace Rodaje.Packages.Download;

public sealed class HttpsRedirectHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    public const int MaxRedirects = 5;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri ?? throw new HttpRequestException("Falta la dirección de descarga.");
        for (int hop = 0; ; hop++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new HttpRequestException("Se rechazó la redirección: solo se permiten destinos HTTPS.");
            using var next = new HttpRequestMessage(request.Method, uri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
            foreach (var header in request.Headers)
            {
                if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            HttpResponseMessage response = await base.SendAsync(next, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            Uri? location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new HttpRequestException("La redirección no indica un destino.");
            if (hop >= MaxRedirects) throw new HttpRequestException("Se superó el máximo de redirecciones HTTPS (5).");
            uri = new Uri(uri, location);
        }
    }
}
