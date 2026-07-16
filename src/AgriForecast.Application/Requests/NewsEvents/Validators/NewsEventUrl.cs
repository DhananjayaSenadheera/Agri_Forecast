namespace AgriForecast.Application.Requests.NewsEvents.Validators;

// Single source of truth for the SourceUrl format rule, shared by the create + update validators.
internal static class NewsEventUrl
{
    // True for an absolute http/https URL. Null/blank returns true (the caller gates with .When so
    // an ABSENT SourceUrl is allowed; only a PRESENT-but-malformed one fails).
    public static bool BeValidAbsoluteHttpUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
