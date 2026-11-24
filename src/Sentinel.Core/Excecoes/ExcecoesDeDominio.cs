namespace Sentinel.Core.Excecoes;

public abstract class ExcecaoDeDominio : Exception
{
    public string Codigo { get; }

    protected ExcecaoDeDominio(string codigo, string mensagem) : base(mensagem)
    {
        Codigo = codigo;
    }
}

public class ExcecaoDeValidacao : ExcecaoDeDominio
{
    public ExcecaoDeValidacao(string mensagem) : base("VALIDATION_ERROR", mensagem) { }
}

public class ExcecaoDeNaoEncontrado : ExcecaoDeDominio
{
    public ExcecaoDeNaoEncontrado(string mensagem) : base("NOT_FOUND", mensagem) { }
}

public class ExcecaoDeConflitoDeVersao : ExcecaoDeDominio
{
    public Guid? TopicoId { get; }
    public int VersaoEsperada { get; }
    public int VersaoAtual { get; }

    public ExcecaoDeConflitoDeVersao(Guid? topicoId, int versaoEsperada, int versaoAtual, string? mensagem = null)
        : base("VERSION_CONFLICT", mensagem ?? $"Conflito de versão no tópico {topicoId}: esperada {versaoEsperada}, mas atual é {versaoAtual}.")
    {
        TopicoId = topicoId;
        VersaoEsperada = versaoEsperada;
        VersaoAtual = versaoAtual;
    }
}

public class ExcecaoDeChaveIdempotenciaReutilizada : ExcecaoDeDominio
{
    public ExcecaoDeChaveIdempotenciaReutilizada(string mensagem = "Chave de idempotência reutilizada com argumentos distintos.")
        : base("IDEMPOTENCY_KEY_REUSED", mensagem) { }
}

public class ExcecaoDeNaoAutorizado : ExcecaoDeDominio
{
    public ExcecaoDeNaoAutorizado(string mensagem = "Acesso não autorizado ou escopo/papel insuficiente.")
        : base("UNAUTHORIZED", mensagem) { }
}

public class ExcecaoDeTemporariamenteIndisponivel : ExcecaoDeDominio
{
    public ExcecaoDeTemporariamenteIndisponivel(string mensagem = "Serviço temporariamente indisponível.")
        : base("TEMPORARILY_UNAVAILABLE", mensagem) { }
}
