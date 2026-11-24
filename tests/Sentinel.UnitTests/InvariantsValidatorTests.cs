using Sentinel.Core.Exceptions;
using Sentinel.Core.Models;
using Sentinel.Core.Validation;
using Xunit;

namespace Sentinel.UnitTests;

public class InvariantsValidatorTests
{
    [Theory]
    [InlineData("valid-slug")]
    [InlineData("payment_idempotency_v2")]
    [InlineData("service.core")]
    public void ValidateTopicKey_ValidSlug_DoesNotThrow(string key)
    {
        InvariantsValidator.ValidateTopicKey(key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid slug with spaces")]
    [InlineData("invalid/slash")]
    public void ValidateTopicKey_InvalidSlug_ThrowsValidationException(string key)
    {
        Assert.Throws<ValidationException>(() => InvariantsValidator.ValidateTopicKey(key));
    }

    [Fact]
    public void ValidateTopicKey_Exceeding80Chars_ThrowsValidationException()
    {
        var longKey = new string('a', 81);
        Assert.Throws<ValidationException>(() => InvariantsValidator.ValidateTopicKey(longKey));
    }

    [Fact]
    public void ValidateTitle_Valid_DoesNotThrow()
    {
        InvariantsValidator.ValidateTitle("Short descriptive title");
    }

    [Fact]
    public void ValidateTitle_Exceeding160Chars_ThrowsValidationException()
    {
        var longTitle = new string('a', 161);
        Assert.Throws<ValidationException>(() => InvariantsValidator.ValidateTitle(longTitle));
    }

    [Fact]
    public void ValidateSummary_Exceeding2000Chars_ThrowsValidationException()
    {
        var longSummary = new string('a', 2001);
        Assert.Throws<ValidationException>(() => InvariantsValidator.ValidateSummary(longSummary));
    }

    [Fact]
    public void ValidateEvidence_RequiresHttps()
    {
        var evidence = new List<Evidence>
        {
            new("pull_request", "PR 42", "http://insecure.example.com/pr/42")
        };

        Assert.Throws<ValidationException>(() => InvariantsValidator.ValidateEvidence(evidence));
    }

    [Fact]
    public void ValidateEvidence_RejectsCredentialsInUrl()
    {
        var evidence = new List<Evidence>
        {
            new("pull_request", "PR 42", "https://user:password@example.com/pr/42")
        };

        Assert.Throws<ValidationException>(() => InvariantsValidator.ValidateEvidence(evidence));
    }

    [Fact]
    public void ValidateEvidence_RejectsSensitiveQueryParams()
    {
        var evidence = new List<Evidence>
        {
            new("pull_request", "PR 42", "https://example.com/pr/42?api_key=secret123")
        };

        Assert.Throws<ValidationException>(() => InvariantsValidator.ValidateEvidence(evidence));
    }

    [Fact]
    public void ValidateEvidence_ValidHttps_DoesNotThrow()
    {
        var evidence = new List<Evidence>
        {
            new("pull_request", "PR 42", "https://github.com/org/repo/pull/42")
        };

        InvariantsValidator.ValidateEvidence(evidence);
    }

    [Fact]
    public void ValidateTechnicalKeys_Max20Allowed()
    {
        var keys = Enumerable.Range(1, 21).Select(i => $"key_{i}").ToList();
        Assert.Throws<ValidationException>(() => InvariantsValidator.ValidateTechnicalKeys(keys));
    }

    [Fact]
    public void ValidateExpectedVersion_Negative_ThrowsValidationException()
    {
        Assert.Throws<ValidationException>(() => InvariantsValidator.ValidateExpectedVersion(-1));
    }
}
