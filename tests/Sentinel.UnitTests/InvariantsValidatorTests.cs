using Sentinel.Core.Excecoes;
using Sentinel.Core.Modelos;
using Sentinel.Core.Validacao;
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
        ValidadorDeInvariantes.ValidarChaveDoTopico(key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid slug with spaces")]
    [InlineData("invalid/slash")]
    public void ValidateTopicKey_InvalidSlug_ThrowsValidationException(string key)
    {
        Assert.Throws<ExcecaoDeValidacao>(() => ValidadorDeInvariantes.ValidarChaveDoTopico(key));
    }

    [Fact]
    public void ValidateTopicKey_Exceeding80Chars_ThrowsValidationException()
    {
        var longKey = new string('a', 81);
        Assert.Throws<ExcecaoDeValidacao>(() => ValidadorDeInvariantes.ValidarChaveDoTopico(longKey));
    }

    [Fact]
    public void ValidateTitle_Valid_DoesNotThrow()
    {
        ValidadorDeInvariantes.ValidarTitulo("Short descriptive title");
    }

    [Fact]
    public void ValidateTitle_Exceeding160Chars_ThrowsValidationException()
    {
        var longTitle = new string('a', 161);
        Assert.Throws<ExcecaoDeValidacao>(() => ValidadorDeInvariantes.ValidarTitulo(longTitle));
    }

    [Fact]
    public void ValidateSummary_Exceeding2000Chars_ThrowsValidationException()
    {
        var longSummary = new string('a', 2001);
        Assert.Throws<ExcecaoDeValidacao>(() => ValidadorDeInvariantes.ValidarResumo(longSummary));
    }

    [Fact]
    public void ValidateEvidence_RequiresHttps()
    {
        var evidence = new List<Evidencia>
        {
            new("pull_request", "PR 42", "http://insecure.example.com/pr/42")
        };

        Assert.Throws<ExcecaoDeValidacao>(() => ValidadorDeInvariantes.ValidarEvidencias(evidence));
    }

    [Fact]
    public void ValidateEvidence_RejectsCredentialsInUrl()
    {
        var evidence = new List<Evidencia>
        {
            new("pull_request", "PR 42", "https://user:password@example.com/pr/42")
        };

        Assert.Throws<ExcecaoDeValidacao>(() => ValidadorDeInvariantes.ValidarEvidencias(evidence));
    }

    [Fact]
    public void ValidateEvidence_RejectsSensitiveQueryParams()
    {
        var evidence = new List<Evidencia>
        {
            new("pull_request", "PR 42", "https://example.com/pr/42?api_key=secret123")
        };

        Assert.Throws<ExcecaoDeValidacao>(() => ValidadorDeInvariantes.ValidarEvidencias(evidence));
    }

    [Fact]
    public void ValidateEvidence_ValidHttps_DoesNotThrow()
    {
        var evidence = new List<Evidencia>
        {
            new("pull_request", "PR 42", "https://github.com/org/repo/pull/42")
        };

        ValidadorDeInvariantes.ValidarEvidencias(evidence);
    }
}
