using System.Text.RegularExpressions;
using Sentinel.Core.Exceptions;
using Sentinel.Core.Models;

namespace Sentinel.Core.Validation;

public static class InvariantsValidator
{
    private static readonly Regex SlugRegex = new(@"^[a-zA-Z0-9_\-\.]{1,80}$", RegexOptions.Compiled);
    private static readonly string[] SensitiveQueryParams = ["token", "secret", "password", "key", "api_key", "access_token"];

    public static void ValidateTopicKey(string? topicKey)
    {
        if (string.IsNullOrWhiteSpace(topicKey))
            throw new ValidationException("topicKey is required.");
        if (topicKey.Length is < 1 or > 80)
            throw new ValidationException("topicKey must be between 1 and 80 characters.");
        if (!SlugRegex.IsMatch(topicKey))
            throw new ValidationException("topicKey must be an ASCII slug (alphanumeric, dashes, underscores, dots).");
    }

    public static void ValidateTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ValidationException("title is required.");
        if (title.Length > 160)
            throw new ValidationException("title must not exceed 160 characters.");
    }

    public static void ValidateSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            throw new ValidationException("summary is required.");
        if (summary.Length > 2000)
            throw new ValidationException("summary must not exceed 2000 characters.");
    }

    public static void ValidateRationale(string? rationale)
    {
        if (rationale != null && rationale.Length > 4000)
            throw new ValidationException("rationale must not exceed 4000 characters.");
    }

    public static void ValidateQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ValidationException("query is required.");
        if (query.Length > 300)
            throw new ValidationException("query must not exceed 300 characters.");
    }

    public static void ValidateEvidence(List<Evidence>? evidence, bool required = true)
    {
        if (evidence == null || evidence.Count == 0)
        {
            if (required)
                throw new ValidationException("At least one evidence item is required.");
            return;
        }

        if (evidence.Count > 10)
            throw new ValidationException("Maximum of 10 evidence items allowed.");

        foreach (var item in evidence)
        {
            if (string.IsNullOrWhiteSpace(item.Type))
                throw new ValidationException("Evidence type is required.");
            if (string.IsNullOrWhiteSpace(item.Label))
                throw new ValidationException("Evidence label is required.");
            if (string.IsNullOrWhiteSpace(item.Url))
                throw new ValidationException("Evidence URL is required.");

            if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new ValidationException($"Evidence URL must be a valid HTTPS URL: '{item.Url}'.");

            if (!string.IsNullOrEmpty(uri.UserInfo))
                throw new ValidationException("Evidence URL must not contain embedded user credentials.");

            if (!string.IsNullOrEmpty(uri.Query))
            {
                var queryLower = uri.Query.ToLowerInvariant();
                foreach (var param in SensitiveQueryParams)
                {
                    if (queryLower.Contains($"{param}="))
                        throw new ValidationException($"Evidence URL contains sensitive parameter: '{param}'.");
                }
            }
        }
    }

    public static void ValidateTechnicalKeys(List<string>? technicalKeys)
    {
        if (technicalKeys == null) return;
        if (technicalKeys.Count > 20)
            throw new ValidationException("Maximum of 20 technical keys allowed.");

        foreach (var key in technicalKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ValidationException("Technical key cannot be empty or whitespace.");
            if (key.Length > 100)
                throw new ValidationException("Technical key cannot exceed 100 characters.");
        }
    }

    public static void ValidateRelatedIncidentIds(List<Guid>? incidentIds)
    {
        if (incidentIds == null) return;
        if (incidentIds.Count > 10)
            throw new ValidationException("Maximum of 10 related incident IDs allowed.");
    }

    public static void ValidateIdempotencyKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ValidationException("idempotencyKey is required.");
        if (idempotencyKey.Length > 128)
            throw new ValidationException("idempotencyKey cannot exceed 128 characters.");
    }

    public static void ValidateLimit(int limit, int min = 1, int max = 20)
    {
        if (limit < min || limit > max)
            throw new ValidationException($"Limit must be between {min} and {max}.");
    }

    public static void ValidateExpectedVersion(int expectedVersion)
    {
        if (expectedVersion < 0)
            throw new ValidationException("expectedVersion must be greater than or equal to 0.");
    }
}
