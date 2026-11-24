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

public class ExcecaoDeConflito : ExcecaoDeDominio
{
    public ExcecaoDeConflito(string mensagem = "Recurso já existente ou conflito com o estado atual.")
        : base("CONFLICT", mensagem) { }
}

public class ExcecaoDeConflitoDeVersao : ExcecaoDeDominio
{
    public Guid? TopicoId { get; }
    public int? VersaoEsperada { get; }
    public int? VersaoAtual { get; }

    public ExcecaoDeConflitoDeVersao(Guid? topicoId, int? versaoEsperada, int? versaoAtual, string? mensagem = null)
        : base("VERSION_CONFLICT", mensagem ?? $"Conflito de versão no tópico {topicoId}: esperada {versaoEsperada}, mas atual é {versaoAtual}.")
    {
        TopicoId = topicoId;
        VersaoEsperada = versaoEsperada;
        VersaoAtual = versaoAtual;
    }
}

public class ExcecaoDePropostaPendente : ExcecaoDeDominio
{
    public Guid TopicoId { get; }
    public Guid PropostaPendenteId { get; }

    public ExcecaoDePropostaPendente(Guid topicoId, Guid propostaPendenteId)
        : base("PROPOSAL_PENDING", $"O tópico {topicoId} já possui a proposta {propostaPendenteId} aguardando revisão. Aguarde a aprovação ou rejeição antes de enviar outra.")
    {
        TopicoId = topicoId;
        PropostaPendenteId = propostaPendenteId;
    }
}

public class ExcecaoDeChaveIdempotenciaReutilizada : ExcecaoDeDominio
{
    public ExcecaoDeChaveIdempotenciaReutilizada(string mensagem = "Chave de idempotência reutilizada com argumentos distintos.")
        : base("IDEMPOTENCY_KEY_REUSED", mensagem) { }
}

// Falha de autenticação
public class ExcecaoDeNaoAutorizado : ExcecaoDeDominio
{
    public ExcecaoDeNaoAutorizado(string mensagem = "Credencial ausente ou inválida.")
        : base("UNAUTHORIZED", mensagem) { }
}

// Falha de autorização e permissão
public class ExcecaoDeAcessoNegado : ExcecaoDeDominio
{
    public ExcecaoDeAcessoNegado(string mensagem = "Escopo ou papel insuficiente para a operação.")
        : base("FORBIDDEN", mensagem) { }
}

public class ExcecaoDeTemporariamenteIndisponivel : ExcecaoDeDominio
{
    public ExcecaoDeTemporariamenteIndisponivel(string mensagem = "Serviço temporariamente indisponível. Tente novamente.")
        : base("TEMPORARILY_UNAVAILABLE", mensagem) { }
}
