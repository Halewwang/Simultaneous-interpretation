using EMKE.Core;

namespace EMKE.Realtime;

public sealed record TranslationEndpointResult
{
    private TranslationEndpointResult(Uri? endpoint, RuntimeError? error)
    {
        Endpoint = endpoint;
        Error = error;
    }

    public bool IsSuccess => Endpoint is not null;

    public Uri? Endpoint { get; }

    public RuntimeError? Error { get; }

    internal static TranslationEndpointResult Success(Uri endpoint)
    {
        return new TranslationEndpointResult(endpoint, null);
    }

    internal static TranslationEndpointResult Failure(RuntimeError error)
    {
        return new TranslationEndpointResult(null, error);
    }
}

public static class TranslationEndpoint
{
    public static TranslationEndpointResult Create(string baseAddress, string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return TranslationEndpointResult.Failure(ConfigurationError(
                "translationEndpoint.invalidModel"));
        }

        if (string.IsNullOrWhiteSpace(baseAddress)
            || !Uri.TryCreate(baseAddress, UriKind.Absolute, out Uri? parsed)
            || string.IsNullOrWhiteSpace(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(parsed.Scheme, "wss", StringComparison.OrdinalIgnoreCase)))
        {
            return TranslationEndpointResult.Failure(ConfigurationError(
                "translationEndpoint.invalidBaseUrl"));
        }

        if (baseAddress.IndexOfAny(['?', '#']) >= 0)
        {
            return TranslationEndpointResult.Failure(ConfigurationError(
                "translationEndpoint.ambiguousBaseUrl"));
        }

        try
        {
            UriBuilder builder = new(parsed)
            {
                Scheme = "wss",
                Path = $"{parsed.AbsolutePath.TrimEnd('/')}/realtime/translations",
                Query = $"model={Uri.EscapeDataString(modelId)}",
                Fragment = string.Empty,
            };
            return TranslationEndpointResult.Success(builder.Uri);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or UriFormatException)
        {
            return TranslationEndpointResult.Failure(ConfigurationError(
                "translationEndpoint.invalidBaseUrl"));
        }
    }

    private static RuntimeError ConfigurationError(string code)
    {
        return new RuntimeError(
            ErrorCategory.Configuration,
            code,
            new Dictionary<string, string>(),
            RecoveryAction.EditSettings);
    }
}
