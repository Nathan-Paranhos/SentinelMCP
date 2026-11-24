using System.Collections.Concurrent;
using System.Text.Json;
using Sentinel.Core.Comandos;
using Sentinel.Core.Consultas;
using Sentinel.Core.Excecoes;
using Sentinel.Core.Modelos;
using Sentinel.Core.Redutores;
using Sentinel.Persistence.Comum;
using Sentinel.Persistence.Contratos;

namespace Sentinel.Persistence.EmMemoria;

public sealed class RepositorioSentinelEmMemoria :
    IRepositorioEspacoTrabalho,
    IRepositorioDecisao,
    IRepositorioIncidente,
    IRepositorioContexto,
    IRepositorioRecibo,
    IReconstrutorProjecoes
{
    private readonly ConcurrentDictionary<Guid, EspacoTrabalho> _espacosTrabalho = new();
    private readonly ConcurrentDictionary<Guid, Projeto> _projetos = new();
    private readonly ConcurrentDictionary<string, VinculoMembro> _vinculosMembros = new();
    private readonly ConcurrentDictionary<Guid, TopicoDecisao> _topicosDecisao = new();
    private readonly ConcurrentDictionary<Guid, FluxoIncidente> _fluxosIncidentes = new();
    private readonly List<EventoMemoria> _eventosMemoria = [];
    private readonly ConcurrentDictionary<Guid, CargaMemoria> _cargasMemoria = new();
    private readonly ConcurrentDictionary<Guid, DecisaoAtual> _decisoesAtuais = new();
    private readonly ConcurrentDictionary<Guid, IncidenteAtual> _incidentesAtuais = new();
    private readonly ConcurrentDictionary<string, ReciboComando> _recibosComando = new();
    private readonly ConcurrentDictionary<Guid, ReciboConsulta> _recibosConsulta = new();
    private readonly ConcurrentDictionary<Guid, List<ItemReciboConsulta>> _itensReciboConsulta = new();

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _travasTopico = new();
    private readonly object _travaGlobal = new();

    public RepositorioSentinelEmMemoria()
    {
    }

    private SemaphoreSlim ObterTravaTopico(Guid espacoTrabalhoId, Guid projetoId, string chaveTopico)
    {
        var chave = $"{espacoTrabalhoId}:{projetoId}:{chaveTopico.ToLowerInvariant()}";
        return _travasTopico.GetOrAdd(chave, _ => new SemaphoreSlim(1, 1));
    }

    public void SemearEspacoTrabalho(EspacoTrabalho espacoTrabalho) => _espacosTrabalho[espacoTrabalho.EspacoTrabalhoId] = espacoTrabalho;
    public void SemearProjeto(Projeto projeto) => _projetos[projeto.ProjetoId] = projeto;
    public void SemearVinculoMembro(VinculoMembro vinculo)
    {
        var chave = $"{vinculo.EspacoTrabalhoId}:{vinculo.Emissor}:{vinculo.Sujeito}";
        _vinculosMembros[chave] = vinculo;
    }

    public void PopularDadosDemonstracao()
    {
        var ws1 = new EspacoTrabalho(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Engenharia Central", "eng-central", DateTimeOffset.UtcNow);
        if (_espacosTrabalho.ContainsKey(ws1.EspacoTrabalhoId))
            return;
        var ws2 = new EspacoTrabalho(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Segurança Isolada", "seg-isolada", DateTimeOffset.UtcNow);
        SemearEspacoTrabalho(ws1);
        SemearEspacoTrabalho(ws2);

        var p1 = new Projeto(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), ws1.EspacoTrabalhoId, "Pagamentos e Cobrança", "pagamentos-cobranca", DateTimeOffset.UtcNow);
        var p2 = new Projeto(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), ws2.EspacoTrabalhoId, "Trilhas de Auditoria", "trilhas-auditoria", DateTimeOffset.UtcNow);
        SemearProjeto(p1);
        SemearProjeto(p2);

        SemearVinculoMembro(new VinculoMembro(Guid.NewGuid(), ws1.EspacoTrabalhoId, "https://auth.sentinel.dev", "agent-dev-1", PapelUsuario.Colaborador, true, DateTimeOffset.UtcNow));
        SemearVinculoMembro(new VinculoMembro(Guid.NewGuid(), ws1.EspacoTrabalhoId, "https://auth.sentinel.dev", "maintainer-alice", PapelUsuario.Mantenedor, true, DateTimeOffset.UtcNow));
        SemearVinculoMembro(new VinculoMembro(Guid.NewGuid(), ws1.EspacoTrabalhoId, "https://auth.sentinel.dev", "reader-bob", PapelUsuario.Leitor, true, DateTimeOffset.UtcNow));
        SemearVinculoMembro(new VinculoMembro(Guid.NewGuid(), ws2.EspacoTrabalhoId, "https://auth.sentinel.dev", "sec-agent-1", PapelUsuario.Colaborador, true, DateTimeOffset.UtcNow));
        SemearVinculoMembro(new VinculoMembro(Guid.NewGuid(), ws2.EspacoTrabalhoId, "https://auth.sentinel.dev", "sec-maintainer", PapelUsuario.Mantenedor, true, DateTimeOffset.UtcNow));

        var incidenteId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var cargaIncidenteId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        _fluxosIncidentes[incidenteId] = new FluxoIncidente(incidenteId, ws1.EspacoTrabalhoId, p1.ProjetoId, 1, DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow.AddDays(-7));

        var cargaIncidente = new CargaMemoria(
            cargaIncidenteId, ws1.EspacoTrabalhoId, p1.ProjetoId,
            "Duplicação de cobrança em retentativas no gateway de pagamento",
            "Clientes sofreram cobrança duplicada após timeouts parciais no webhook do gateway externo.",
            "O webhook do gateway externo falhou com timeout de rede e realizou nova tentativa de entrega sem que a transação anterior tivesse sido reconciliada com chave única no banco.",
            ["BillingService", "WebhookController", "charge_attempts", "ERR_DUPLICATE_CHARGE"],
            [],
            [
                new Evidencia("incident_rca", "RCA Cobrança Duplicada 2026-10", "https://example.com/rca/billing-dup-202610"),
                new Evidencia("pull_request", "PR Correção da duplicação", "https://example.com/pull/42")
            ],
            DateTimeOffset.UtcNow.AddDays(-7)
        );
        _cargasMemoria[cargaIncidenteId] = cargaIncidente;

        var eventoIncidente = new EventoMemoria(
            Guid.NewGuid(), ws1.EspacoTrabalhoId, p1.ProjetoId, incidenteId, 1, "IncidentReported", 1,
            DateTimeOffset.UtcNow.AddDays(-7), "agent-dev-1", "https://auth.sentinel.dev", "claude-code", cargaIncidenteId, null
        );
        _eventosMemoria.Add(eventoIncidente);

        _incidentesAtuais[incidenteId] = new IncidenteAtual(
            incidenteId, ws1.EspacoTrabalhoId, p1.ProjetoId, 1, 1, StatusIncidente.Reportado,
            cargaIncidente.Titulo, cargaIncidente.Resumo, cargaIncidente.CriadoEm,
            cargaIncidente.ChavesTecnicas, cargaIncidente.Evidencias, cargaIncidente.CriadoEm
        );

        var topicoId = Guid.Parse("c0c58a62-e095-4baf-9b56-69de5fa339ab");
        var cargaDecisaoId = Guid.Parse("ed377b6a-cfdb-4c94-a51a-d0a0f66a247a");
        _topicosDecisao[topicoId] = new TopicoDecisao(topicoId, ws1.EspacoTrabalhoId, p1.ProjetoId, "payment-idempotency", 2, DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow.AddDays(-4));

        var cargaDecisao = new CargaMemoria(
            cargaDecisaoId, ws1.EspacoTrabalhoId, p1.ProjetoId,
            "Idempotência Obrigatória para Cobranças",
            "Usar uma chave de idempotência por tentativa lógica de cobrança com persistência transacional.",
            "Evitar cobranças duplicadas em timeouts do gateway e garantir retentativas seguras por agentes e serviços.",
            ["BillingService", "payment-idempotency", "IdempotencyKey", "charge_attempts"],
            [incidenteId],
            [
                new Evidencia("adr", "ADR 003 Idempotência de Pagamentos", "https://example.com/adr/003-payment-idempotency"),
                new Evidencia("pull_request", "PR 42 Implementação de idempotência", "https://example.com/pull/42")
            ],
            DateTimeOffset.UtcNow.AddDays(-5)
        );
        _cargasMemoria[cargaDecisaoId] = cargaDecisao;

        var eventoProposta = new EventoMemoria(
            Guid.NewGuid(), ws1.EspacoTrabalhoId, p1.ProjetoId, topicoId, 1, "DecisionProposed", 1,
            DateTimeOffset.UtcNow.AddDays(-5), "agent-dev-1", "https://auth.sentinel.dev", "claude-code", cargaDecisaoId, null
        );
        var eventoAprovacao = new EventoMemoria(
            Guid.NewGuid(), ws1.EspacoTrabalhoId, p1.ProjetoId, topicoId, 2, "DecisionApproved", 1,
            DateTimeOffset.UtcNow.AddDays(-4), "maintainer-alice", "https://auth.sentinel.dev", null, null, eventoProposta.EventoId
        );
        _eventosMemoria.Add(eventoProposta);
        _eventosMemoria.Add(eventoAprovacao);

        _decisoesAtuais[topicoId] = new DecisaoAtual(
            topicoId, ws1.EspacoTrabalhoId, p1.ProjetoId, "payment-idempotency",
            cargaDecisaoId, 1, 2, StatusDecisao.Ativa, null,
            cargaDecisao.Titulo, cargaDecisao.Resumo, cargaDecisao.Justificativa,
            cargaDecisao.ChavesTecnicas, cargaDecisao.IncidentesRelacionadosIds,
            cargaDecisao.Evidencias, DateTimeOffset.UtcNow.AddDays(-4)
        );
    }

    public Task<EspacoTrabalho?> ObterEspacoTrabalhoAsync(Guid espacoTrabalhoId, CancellationToken ct = default)
    {
        _espacosTrabalho.TryGetValue(espacoTrabalhoId, out var ws);
        return Task.FromResult(ws);
    }

    public Task<VinculoMembro?> ObterVinculoMembroAsync(Guid espacoTrabalhoId, string emissor, string sujeito, CancellationToken ct = default)
    {
        var chave = $"{espacoTrabalhoId}:{emissor}:{sujeito}";
        _vinculosMembros.TryGetValue(chave, out var vm);
        return Task.FromResult(vm);
    }

    public Task<Projeto?> ObterProjetoAsync(Guid espacoTrabalhoId, Guid projetoId, CancellationToken ct = default)
    {
        if (_projetos.TryGetValue(projetoId, out var p) && p.EspacoTrabalhoId == espacoTrabalhoId)
            return Task.FromResult<Projeto?>(p);
        return Task.FromResult<Projeto?>(null);
    }

    public Task<List<Projeto>> ObterProjetosPorEspacoTrabalhoAsync(Guid espacoTrabalhoId, CancellationToken ct = default)
    {
        var lista = _projetos.Values.Where(p => p.EspacoTrabalhoId == espacoTrabalhoId).ToList();
        return Task.FromResult(lista);
    }

    public Task<TopicoDecisao?> ObterTopicoPorChaveAsync(Guid espacoTrabalhoId, Guid projetoId, string chaveTopico, CancellationToken ct = default)
    {
        var topico = _topicosDecisao.Values.FirstOrDefault(t =>
            t.EspacoTrabalhoId == espacoTrabalhoId &&
            t.ProjetoId == projetoId &&
            string.Equals(t.ChaveTopico, chaveTopico, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(topico);
    }

    public Task<TopicoDecisao?> ObterTopicoPorIdAsync(Guid espacoTrabalhoId, Guid projetoId, Guid topicoId, CancellationToken ct = default)
    {
        if (_topicosDecisao.TryGetValue(topicoId, out var t) &&
            t.EspacoTrabalhoId == espacoTrabalhoId &&
            t.ProjetoId == projetoId)
        {
            return Task.FromResult<TopicoDecisao?>(t);
        }
        return Task.FromResult<TopicoDecisao?>(null);
    }

    public Task<DecisaoAtual?> ObterDecisaoAtualAsync(Guid espacoTrabalhoId, Guid projetoId, Guid topicoId, CancellationToken ct = default)
    {
        if (_decisoesAtuais.TryGetValue(topicoId, out var d) &&
            d.EspacoTrabalhoId == espacoTrabalhoId &&
            d.ProjetoId == projetoId)
        {
            return Task.FromResult<DecisaoAtual?>(d);
        }
        return Task.FromResult<DecisaoAtual?>(null);
    }

    public Task<List<DecisaoAtual>> ObterPropostasPendentesAsync(Guid espacoTrabalhoId, Guid? projetoId = null, CancellationToken ct = default)
    {
        var query = _decisoesAtuais.Values
            .Where(d => d.EspacoTrabalhoId == espacoTrabalhoId && d.PropostaPendenteId != null);

        if (projetoId.HasValue)
            query = query.Where(d => d.ProjetoId == projetoId.Value);

        return Task.FromResult(query.ToList());
    }

    public Task<List<EventoMemoria>> ObterEventosPorFluxoAsync(Guid espacoTrabalhoId, Guid fluxoId, int? naVersao = null, int? antesDaVersao = null, int limite = 50, CancellationToken ct = default)
    {
        lock (_travaGlobal)
        {
            var query = _eventosMemoria
                .Where(e => e.EspacoTrabalhoId == espacoTrabalhoId && e.FluxoId == fluxoId);

            if (naVersao.HasValue)
                query = query.Where(e => e.VersaoFluxo <= naVersao.Value);

            if (antesDaVersao.HasValue)
                query = query.Where(e => e.VersaoFluxo < antesDaVersao.Value);

            var eventos = query
                .OrderBy(e => e.VersaoFluxo)
                .Take(limite)
                .ToList();

            return Task.FromResult(eventos);
        }
    }

    public Task<CargaMemoria?> ObterCargaAsync(Guid espacoTrabalhoId, Guid cargaId, CancellationToken ct = default)
    {
        if (_cargasMemoria.TryGetValue(cargaId, out var c) && c.EspacoTrabalhoId == espacoTrabalhoId)
            return Task.FromResult<CargaMemoria?>(c);
        return Task.FromResult<CargaMemoria?>(null);
    }

    public async Task<ResultadoProporDecisao> ProporDecisaoAsync(ComandoProporDecisao comando, CancellationToken ct = default)
    {
        var trava = ObterTravaTopico(comando.EspacoTrabalhoId, comando.ProjetoId, comando.ChaveTopico);
        await trava.WaitAsync(ct);
        try
        {
            var chaveRecibo = $"{comando.EspacoTrabalhoId}:{comando.AutorSujeito}:propose_decision:{comando.ChaveIdempotencia}";
            var hashArgumentos = AjudanteIdempotencia.CalcularImpressaoDigital(new
            {
                comando.EspacoTrabalhoId,
                comando.ProjetoId,
                comando.ChaveTopico,
                comando.VersaoEsperada,
                comando.Titulo,
                comando.Resumo,
                comando.Justificativa,
                comando.ChavesTecnicas,
                comando.IncidentesRelacionadosIds,
                comando.Evidencias
            });

            if (_recibosComando.TryGetValue(chaveRecibo, out var reciboExistente))
            {
                if (reciboExistente.HashArgumentos != hashArgumentos)
                    throw new ExcecaoDeChaveIdempotenciaReutilizada("A chave de idempotência foi reutilizada com argumentos distintos.");

                return JsonSerializer.Deserialize<ResultadoProporDecisao>(reciboExistente.ResultadoSerializado)!;
            }

            var topicoExistente = _topicosDecisao.Values.FirstOrDefault(t =>
                t.EspacoTrabalhoId == comando.EspacoTrabalhoId &&
                t.ProjetoId == comando.ProjetoId &&
                string.Equals(t.ChaveTopico, comando.ChaveTopico, StringComparison.OrdinalIgnoreCase));

            int versaoAtual = topicoExistente?.VersaoAtual ?? 0;
            var topicoId = topicoExistente?.TopicoId ?? Guid.NewGuid();

            if (comando.VersaoEsperada != versaoAtual)
            {
                throw new ExcecaoDeConflitoDeVersao(topicoId, comando.VersaoEsperada, versaoAtual);
            }

            var novaVersao = versaoAtual + 1;
            var agora = DateTimeOffset.UtcNow;

            if (topicoExistente == null)
            {
                topicoExistente = new TopicoDecisao(topicoId, comando.EspacoTrabalhoId, comando.ProjetoId, comando.ChaveTopico, novaVersao, agora, agora);
            }
            else
            {
                topicoExistente = topicoExistente with { VersaoAtual = novaVersao, AtualizadoEm = agora };
            }
            _topicosDecisao[topicoId] = topicoExistente;

            var cargaId = Guid.NewGuid();
            var carga = new CargaMemoria(
                cargaId, comando.EspacoTrabalhoId, comando.ProjetoId,
                comando.Titulo, comando.Resumo, comando.Justificativa,
                comando.ChavesTecnicas ?? [], comando.IncidentesRelacionadosIds ?? [],
                comando.Evidencias, agora
            );
            _cargasMemoria[cargaId] = carga;

            var evento = new EventoMemoria(
                Guid.NewGuid(), comando.EspacoTrabalhoId, comando.ProjetoId,
                topicoId, novaVersao, "DecisionProposed", 1, agora,
                comando.AutorSujeito, comando.AutorEmissor, comando.ClienteId,
                cargaId, null
            );

            lock (_travaGlobal)
            {
                _eventosMemoria.Add(evento);
            }

            _decisoesAtuais.TryGetValue(topicoId, out var atual);
            if (atual == null)
            {
                _decisoesAtuais[topicoId] = new DecisaoAtual(
                    topicoId, comando.EspacoTrabalhoId, comando.ProjetoId, comando.ChaveTopico,
                    null, null, novaVersao, StatusDecisao.Proposta, cargaId,
                    carga.Titulo, carga.Resumo, carga.Justificativa,
                    carga.ChavesTecnicas, carga.IncidentesRelacionadosIds,
                    carga.Evidencias, agora
                );
            }
            else
            {
                _decisoesAtuais[topicoId] = atual with
                {
                    VersaoFluxo = novaVersao,
                    PropostaPendenteId = cargaId,
                    AtualizadoEm = agora
                };
            }

            var resultado = new ResultadoProporDecisao(topicoId, cargaId, novaVersao, "proposed");
            var recibo = new ReciboComando(
                Guid.NewGuid(), comando.EspacoTrabalhoId, comando.AutorSujeito,
                "propose_decision", comando.ChaveIdempotencia, hashArgumentos,
                JsonSerializer.Serialize(resultado), agora
            );
            _recibosComando[chaveRecibo] = recibo;

            return resultado;
        }
        finally
        {
            trava.Release();
        }
    }

    public async Task<ResultadoAprovarDecisao> AprovarDecisaoAsync(ComandoAprovarDecisao comando, CancellationToken ct = default)
    {
        if (!_topicosDecisao.TryGetValue(comando.TopicoId, out var topico) ||
            topico.EspacoTrabalhoId != comando.EspacoTrabalhoId ||
            topico.ProjetoId != comando.ProjetoId)
        {
            throw new ExcecaoDeNaoEncontrado("Tópico de decisão não encontrado.");
        }

        var trava = ObterTravaTopico(comando.EspacoTrabalhoId, comando.ProjetoId, topico.ChaveTopico);
        await trava.WaitAsync(ct);
        try
        {
            var chaveRecibo = $"{comando.EspacoTrabalhoId}:{comando.RevisorSujeito}:approve_decision:{comando.ChaveIdempotencia}";
            var hashArgumentos = AjudanteIdempotencia.CalcularImpressaoDigital(new
            {
                comando.EspacoTrabalhoId,
                comando.ProjetoId,
                comando.TopicoId,
                comando.PropostaId,
                comando.VersaoEsperada
            });

            if (_recibosComando.TryGetValue(chaveRecibo, out var reciboExistente))
            {
                if (reciboExistente.HashArgumentos != hashArgumentos)
                    throw new ExcecaoDeChaveIdempotenciaReutilizada("A chave de idempotência foi reutilizada com argumentos distintos.");

                return JsonSerializer.Deserialize<ResultadoAprovarDecisao>(reciboExistente.ResultadoSerializado)!;
            }

            if (comando.VersaoEsperada != topico.VersaoAtual)
            {
                throw new ExcecaoDeConflitoDeVersao(comando.TopicoId, comando.VersaoEsperada, topico.VersaoAtual);
            }

            if (!_decisoesAtuais.TryGetValue(comando.TopicoId, out var atual) ||
                atual.PropostaPendenteId != comando.PropostaId)
            {
                throw new ExcecaoDeValidacao("Não existe proposta pendente com o ID informado para aprovação.");
            }

            if (!_cargasMemoria.TryGetValue(comando.PropostaId, out var cargaProposta))
            {
                throw new ExcecaoDeNaoEncontrado("Carga da proposta não encontrada.");
            }

            var novaVersao = topico.VersaoAtual + 1;
            var agora = DateTimeOffset.UtcNow;

            topico = topico with { VersaoAtual = novaVersao, AtualizadoEm = agora };
            _topicosDecisao[comando.TopicoId] = topico;

            var eventoPropostaOriginal = _eventosMemoria.First(e =>
                e.EspacoTrabalhoId == comando.EspacoTrabalhoId &&
                e.FluxoId == comando.TopicoId &&
                e.CargaId == comando.PropostaId);

            var eventoAprovacao = new EventoMemoria(
                Guid.NewGuid(), comando.EspacoTrabalhoId, comando.ProjetoId,
                comando.TopicoId, novaVersao, "DecisionApproved", 1, agora,
                comando.RevisorSujeito, comando.RevisorEmissor, null,
                null, eventoPropostaOriginal.EventoId
            );

            lock (_travaGlobal)
            {
                _eventosMemoria.Add(eventoAprovacao);
            }

            var novaVersaoRegistro = (atual.VersaoRegistroAtivo ?? 0) + 1;
            _decisoesAtuais[comando.TopicoId] = new DecisaoAtual(
                comando.TopicoId, comando.EspacoTrabalhoId, comando.ProjetoId, topico.ChaveTopico,
                comando.PropostaId, novaVersaoRegistro, novaVersao, StatusDecisao.Ativa, null,
                cargaProposta.Titulo, cargaProposta.Resumo, cargaProposta.Justificativa,
                cargaProposta.ChavesTecnicas, cargaProposta.IncidentesRelacionadosIds,
                cargaProposta.Evidencias, agora
            );

            var resultado = new ResultadoAprovarDecisao(comando.TopicoId, comando.PropostaId, novaVersao, "active");
            var recibo = new ReciboComando(
                Guid.NewGuid(), comando.EspacoTrabalhoId, comando.RevisorSujeito,
                "approve_decision", comando.ChaveIdempotencia, hashArgumentos,
                JsonSerializer.Serialize(resultado), agora
            );
            _recibosComando[chaveRecibo] = recibo;

            return resultado;
        }
        finally
        {
            trava.Release();
        }
    }

    public async Task<ResultadoRejeitarDecisao> RejeitarDecisaoAsync(ComandoRejeitarDecisao comando, CancellationToken ct = default)
    {
        if (!_topicosDecisao.TryGetValue(comando.TopicoId, out var topico) ||
            topico.EspacoTrabalhoId != comando.EspacoTrabalhoId ||
            topico.ProjetoId != comando.ProjetoId)
        {
            throw new ExcecaoDeNaoEncontrado("Tópico de decisão não encontrado.");
        }

        var trava = ObterTravaTopico(comando.EspacoTrabalhoId, comando.ProjetoId, topico.ChaveTopico);
        await trava.WaitAsync(ct);
        try
        {
            var chaveRecibo = $"{comando.EspacoTrabalhoId}:{comando.RevisorSujeito}:reject_decision:{comando.ChaveIdempotencia}";
            var hashArgumentos = AjudanteIdempotencia.CalcularImpressaoDigital(new
            {
                comando.EspacoTrabalhoId,
                comando.ProjetoId,
                comando.TopicoId,
                comando.PropostaId,
                comando.VersaoEsperada,
                comando.Motivo
            });

            if (_recibosComando.TryGetValue(chaveRecibo, out var reciboExistente))
            {
                if (reciboExistente.HashArgumentos != hashArgumentos)
                    throw new ExcecaoDeChaveIdempotenciaReutilizada("A chave de idempotência foi reutilizada com argumentos distintos.");

                return JsonSerializer.Deserialize<ResultadoRejeitarDecisao>(reciboExistente.ResultadoSerializado)!;
            }

            if (comando.VersaoEsperada != topico.VersaoAtual)
            {
                throw new ExcecaoDeConflitoDeVersao(comando.TopicoId, comando.VersaoEsperada, topico.VersaoAtual);
            }

            if (!_decisoesAtuais.TryGetValue(comando.TopicoId, out var atual) ||
                atual.PropostaPendenteId != comando.PropostaId)
            {
                throw new ExcecaoDeValidacao("Não existe proposta pendente com o ID informado para rejeição.");
            }

            var novaVersao = topico.VersaoAtual + 1;
            var agora = DateTimeOffset.UtcNow;

            topico = topico with { VersaoAtual = novaVersao, AtualizadoEm = agora };
            _topicosDecisao[comando.TopicoId] = topico;

            var eventoProposta = _eventosMemoria.First(e =>
                e.EspacoTrabalhoId == comando.EspacoTrabalhoId &&
                e.FluxoId == comando.TopicoId &&
                e.CargaId == comando.PropostaId);

            var eventoRejeicao = new EventoMemoria(
                Guid.NewGuid(), comando.EspacoTrabalhoId, comando.ProjetoId,
                comando.TopicoId, novaVersao, "DecisionRejected", 1, agora,
                comando.RevisorSujeito, comando.RevisorEmissor, null,
                null, eventoProposta.EventoId
            );

            lock (_travaGlobal)
            {
                _eventosMemoria.Add(eventoRejeicao);
            }

            _decisoesAtuais[comando.TopicoId] = atual with
            {
                VersaoFluxo = novaVersao,
                PropostaPendenteId = null,
                AtualizadoEm = agora
            };

            var statusResultado = atual.RegistroAtivoId.HasValue ? "active" : "superseded";
            var resultado = new ResultadoRejeitarDecisao(comando.TopicoId, novaVersao, statusResultado);
            var recibo = new ReciboComando(
                Guid.NewGuid(), comando.EspacoTrabalhoId, comando.RevisorSujeito,
                "reject_decision", comando.ChaveIdempotencia, hashArgumentos,
                JsonSerializer.Serialize(resultado), agora
            );
            _recibosComando[chaveRecibo] = recibo;

            return resultado;
        }
        finally
        {
            trava.Release();
        }
    }

    public async Task<ResultadoRestaurarDecisao> RestaurarDecisaoAsync(ComandoRestaurarDecisao comando, CancellationToken ct = default)
    {
        if (!_topicosDecisao.TryGetValue(comando.TopicoId, out var topico) ||
            topico.EspacoTrabalhoId != comando.EspacoTrabalhoId ||
            topico.ProjetoId != comando.ProjetoId)
        {
            throw new ExcecaoDeNaoEncontrado("Tópico de decisão não encontrado.");
        }

        var trava = ObterTravaTopico(comando.EspacoTrabalhoId, comando.ProjetoId, topico.ChaveTopico);
        await trava.WaitAsync(ct);
        try
        {
            var chaveRecibo = $"{comando.EspacoTrabalhoId}:{comando.RevisorSujeito}:restore_decision:{comando.ChaveIdempotencia}";
            var hashArgumentos = AjudanteIdempotencia.CalcularImpressaoDigital(new
            {
                comando.EspacoTrabalhoId,
                comando.ProjetoId,
                comando.TopicoId,
                comando.VersaoParaRestaurar,
                comando.VersaoEsperada
            });

            if (_recibosComando.TryGetValue(chaveRecibo, out var reciboExistente))
            {
                if (reciboExistente.HashArgumentos != hashArgumentos)
                    throw new ExcecaoDeChaveIdempotenciaReutilizada("A chave de idempotência foi reutilizada com argumentos distintos.");

                return JsonSerializer.Deserialize<ResultadoRestaurarDecisao>(reciboExistente.ResultadoSerializado)!;
            }

            if (comando.VersaoEsperada != topico.VersaoAtual)
            {
                throw new ExcecaoDeConflitoDeVersao(comando.TopicoId, comando.VersaoEsperada, topico.VersaoAtual);
            }

            var eventosAteVersao = _eventosMemoria
                .Where(e => e.EspacoTrabalhoId == comando.EspacoTrabalhoId &&
                            e.FluxoId == comando.TopicoId &&
                            e.VersaoFluxo <= comando.VersaoParaRestaurar)
                .OrderBy(e => e.VersaoFluxo)
                .ToList();

            if (eventosAteVersao.Count == 0)
                throw new ExcecaoDeNaoEncontrado("Nenhum histórico encontrado para a versão solicitada.");

            var snapshot = RedutorDeDecisao.Reconstituir(comando.TopicoId, comando.EspacoTrabalhoId, comando.ProjetoId, topico.ChaveTopico, eventosAteVersao, id => _cargasMemoria.TryGetValue(id, out var c) ? c : null, comando.VersaoParaRestaurar);
            if (snapshot.RegistroAtivoId == null)
                throw new ExcecaoDeValidacao("A versão indicada não possui decisão ativa para restauração.");

            if (!_cargasMemoria.TryGetValue(snapshot.RegistroAtivoId.Value, out var cargaOriginal))
                throw new ExcecaoDeNaoEncontrado("Carga histórica da decisão não encontrada.");

            var novaVersao = topico.VersaoAtual + 1;
            var agora = DateTimeOffset.UtcNow;

            topico = topico with { VersaoAtual = novaVersao, AtualizadoEm = agora };
            _topicosDecisao[comando.TopicoId] = topico;

            var novaCargaId = Guid.NewGuid();
            var novaCarga = new CargaMemoria(
                novaCargaId, comando.EspacoTrabalhoId, comando.ProjetoId,
                cargaOriginal.Titulo, cargaOriginal.Resumo, cargaOriginal.Justificativa,
                cargaOriginal.ChavesTecnicas, cargaOriginal.IncidentesRelacionadosIds,
                cargaOriginal.Evidencias, agora
            );
            _cargasMemoria[novaCargaId] = novaCarga;

            var eventoProposta = new EventoMemoria(
                Guid.NewGuid(), comando.EspacoTrabalhoId, comando.ProjetoId,
                comando.TopicoId, novaVersao, "DecisionProposed", 1, agora,
                comando.RevisorSujeito, comando.RevisorEmissor, null,
                novaCargaId, null
            );

            lock (_travaGlobal)
            {
                _eventosMemoria.Add(eventoProposta);
            }

            _decisoesAtuais.TryGetValue(comando.TopicoId, out var atual);
            if (atual != null)
            {
                _decisoesAtuais[comando.TopicoId] = atual with
                {
                    VersaoFluxo = novaVersao,
                    PropostaPendenteId = novaCargaId,
                    AtualizadoEm = agora
                };
            }

            var resultado = new ResultadoRestaurarDecisao(comando.TopicoId, novaCargaId, novaVersao, "proposed");
            var recibo = new ReciboComando(
                Guid.NewGuid(), comando.EspacoTrabalhoId, comando.RevisorSujeito,
                "restore_decision", comando.ChaveIdempotencia, hashArgumentos,
                JsonSerializer.Serialize(resultado), agora
            );
            _recibosComando[chaveRecibo] = recibo;

            return resultado;
        }
        finally
        {
            trava.Release();
        }
    }

    public Task<FluxoIncidente?> ObterIncidentePorIdAsync(Guid espacoTrabalhoId, Guid projetoId, Guid incidenteId, CancellationToken ct = default)
    {
        if (_fluxosIncidentes.TryGetValue(incidenteId, out var inc) &&
            inc.EspacoTrabalhoId == espacoTrabalhoId &&
            inc.ProjetoId == projetoId)
        {
            return Task.FromResult<FluxoIncidente?>(inc);
        }
        return Task.FromResult<FluxoIncidente?>(null);
    }

    public Task<IncidenteAtual?> ObterIncidenteAtualAsync(Guid espacoTrabalhoId, Guid projetoId, Guid incidenteId, CancellationToken ct = default)
    {
        if (_incidentesAtuais.TryGetValue(incidenteId, out var inc) &&
            inc.EspacoTrabalhoId == espacoTrabalhoId &&
            inc.ProjetoId == projetoId)
        {
            return Task.FromResult<IncidenteAtual?>(inc);
        }
        return Task.FromResult<IncidenteAtual?>(null);
    }

    public Task<ResultadoRegistrarIncidente> RegistrarIncidenteAsync(ComandoRegistrarIncidente comando, CancellationToken ct = default)
    {
        var chaveRecibo = $"{comando.EspacoTrabalhoId}:{comando.AutorSujeito}:log_incident:{comando.ChaveIdempotencia}";
        var hashArgumentos = AjudanteIdempotencia.CalcularImpressaoDigital(new
        {
            comando.EspacoTrabalhoId,
            comando.ProjetoId,
            comando.Titulo,
            comando.Resumo,
            comando.OcorridoEm,
            comando.ChavesTecnicas,
            comando.Evidencias
        });

        lock (_travaGlobal)
        {
            if (_recibosComando.TryGetValue(chaveRecibo, out var reciboExistente))
            {
                if (reciboExistente.HashArgumentos != hashArgumentos)
                    throw new ExcecaoDeChaveIdempotenciaReutilizada("A chave de idempotência foi reutilizada com argumentos distintos.");

                return Task.FromResult(JsonSerializer.Deserialize<ResultadoRegistrarIncidente>(reciboExistente.ResultadoSerializado)!);
            }

            var incidenteId = Guid.NewGuid();
            var cargaId = Guid.NewGuid();
            var agora = DateTimeOffset.UtcNow;
            var ocorridoEm = comando.OcorridoEm ?? agora;

            var fluxoIncidente = new FluxoIncidente(incidenteId, comando.EspacoTrabalhoId, comando.ProjetoId, 1, agora, agora);
            _fluxosIncidentes[incidenteId] = fluxoIncidente;

            var carga = new CargaMemoria(
                cargaId, comando.EspacoTrabalhoId, comando.ProjetoId,
                comando.Titulo, comando.Resumo, null,
                comando.ChavesTecnicas ?? [], [], comando.Evidencias, agora
            );
            _cargasMemoria[cargaId] = carga;

            var evento = new EventoMemoria(
                Guid.NewGuid(), comando.EspacoTrabalhoId, comando.ProjetoId,
                incidenteId, 1, "IncidentReported", 1, agora,
                comando.AutorSujeito, comando.AutorEmissor, comando.ClienteId,
                cargaId, null
            );
            _eventosMemoria.Add(evento);

            _incidentesAtuais[incidenteId] = new IncidenteAtual(
                incidenteId, comando.EspacoTrabalhoId, comando.ProjetoId,
                1, 1, StatusIncidente.Reportado,
                carga.Titulo, carga.Resumo, ocorridoEm,
                carga.ChavesTecnicas, carga.Evidencias, agora
            );

            var resultado = new ResultadoRegistrarIncidente(incidenteId, 1, "reported");
            var recibo = new ReciboComando(
                Guid.NewGuid(), comando.EspacoTrabalhoId, comando.AutorSujeito,
                "log_incident", comando.ChaveIdempotencia, hashArgumentos,
                JsonSerializer.Serialize(resultado), agora
            );
            _recibosComando[chaveRecibo] = recibo;

            return Task.FromResult(resultado);
        }
    }

    public Task<ResultadoConsultaContexto> ConsultarContextoAsync(ConsultaContexto consulta, CancellationToken ct = default)
    {
        lock (_travaGlobal)
        {
            var consultaId = Guid.NewGuid();
            var agora = DateTimeOffset.UtcNow;
            var termos = consulta.TermoBusca.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var itens = new List<ItemContexto>();
            var itensRecibo = new List<ItemReciboConsulta>();

            if (consulta.TipoRegistro is "decision" or "both")
            {
                var decisoesValidas = _decisoesAtuais.Values
                    .Where(d => d.EspacoTrabalhoId == consulta.EspacoTrabalhoId &&
                                d.ProjetoId == consulta.ProjetoId &&
                                d.Situacao == StatusDecisao.Ativa &&
                                d.RegistroAtivoId.HasValue)
                    .ToList();

                foreach (var decisao in decisoesValidas)
                {
                    int pontuacao = CalcularPontuacaoBusca(decisao.Titulo, decisao.Resumo, decisao.Justificativa, decisao.ChavesTecnicas, termos);
                    if (pontuacao > 0)
                    {
                        itens.Add(new ItemContexto(
                            "decision",
                            decisao.TopicoId,
                            null,
                            decisao.RegistroAtivoId!.Value,
                            decisao.VersaoRegistroAtivo ?? 1,
                            decisao.VersaoFluxo,
                            "active",
                            decisao.Titulo,
                            decisao.Resumo,
                            decisao.Evidencias
                        ));
                    }
                }
            }

            if (consulta.TipoRegistro is "incident" or "both")
            {
                var incidentesValidos = _incidentesAtuais.Values
                    .Where(i => i.EspacoTrabalhoId == consulta.EspacoTrabalhoId &&
                                i.ProjetoId == consulta.ProjetoId)
                    .ToList();

                foreach (var inc in incidentesValidos)
                {
                    int pontuacao = CalcularPontuacaoBusca(inc.Titulo, inc.Resumo, null, inc.ChavesTecnicas, termos);
                    if (pontuacao > 0)
                    {
                        itens.Add(new ItemContexto(
                            "incident",
                            null,
                            inc.IncidenteId,
                            inc.IncidenteId,
                            inc.VersaoRegistro,
                            inc.VersaoFluxo,
                            "reported",
                            inc.Titulo,
                            inc.Resumo,
                            inc.Evidencias
                        ));
                    }
                }
            }

            var itensOrdenados = itens.OrderByDescending(i =>
            {
                int p = 0;
                foreach (var t in termos)
                {
                    var carga = _decisoesAtuais.Values.FirstOrDefault(d => d.TopicoId == i.TopicoId);
                    if (carga != null && carga.ChavesTecnicas.Any(k => string.Equals(k, t, StringComparison.OrdinalIgnoreCase)))
                        p += 100;
                }
                return p;
            }).ToList();

            var itensLimitados = itensOrdenados.Take(consulta.Limite).ToList();
            int posicao = 1;
            foreach (var item in itensLimitados)
            {
                itensRecibo.Add(new ItemReciboConsulta(
                    Guid.NewGuid(),
                    consultaId,
                    consulta.EspacoTrabalhoId,
                    posicao++,
                    item.TipoRegistro,
                    item.TopicoId ?? item.IncidenteId!.Value,
                    item.RegistroId,
                    item.VersaoRegistro,
                    item.VersaoFluxo,
                    item.Situacao
                ));
            }

            var reciboConsulta = new ReciboConsulta(
                consultaId,
                consulta.EspacoTrabalhoId,
                consulta.ProjetoId,
                consulta.PrincipalId,
                consulta.ClienteId,
                "textual",
                agora,
                itensRecibo
            );
            _recibosConsulta[consultaId] = reciboConsulta;
            _itensReciboConsulta[consultaId] = itensRecibo;

            var statusCorrespondencia = itensLimitados.Count > 0 ? "found" : "none";
            var resultado = new ResultadoConsultaContexto(consultaId, statusCorrespondencia, "textual", itensLimitados);
            return Task.FromResult(resultado);
        }
    }

    private static int CalcularPontuacaoBusca(string titulo, string resumo, string? justificativa, List<string> chavesTecnicas, string[] termos)
    {
        int pontuacao = 0;
        foreach (var termo in termos)
        {
            if (chavesTecnicas.Any(k => k.Contains(termo, StringComparison.OrdinalIgnoreCase))) pontuacao += 10;
            if (titulo.Contains(termo, StringComparison.OrdinalIgnoreCase)) pontuacao += 5;
            if (resumo.Contains(termo, StringComparison.OrdinalIgnoreCase)) pontuacao += 2;
            if (justificativa != null && justificativa.Contains(termo, StringComparison.OrdinalIgnoreCase)) pontuacao += 1;
        }
        return pontuacao;
    }

    public Task<ResultadoComprovanteContexto> ObterComprovanteContextoAsync(Guid espacoTrabalhoId, Guid projetoId, Guid consultaId, string principalId, CancellationToken ct = default)
    {
        lock (_travaGlobal)
        {
            if (!_recibosConsulta.TryGetValue(consultaId, out var recibo) ||
                recibo.EspacoTrabalhoId != espacoTrabalhoId ||
                recibo.ProjetoId != projetoId)
            {
                throw new ExcecaoDeNaoEncontrado("Comprovante de consulta não encontrado.");
            }

            _itensReciboConsulta.TryGetValue(consultaId, out var itens);
            var resultado = new ResultadoComprovanteContexto(
                recibo.ConsultaId,
                recibo.EspacoTrabalhoId,
                recibo.ProjetoId,
                recibo.PrincipalId,
                recibo.ClienteId,
                recibo.ModoBusca,
                recibo.CriadoEm,
                itens ?? []
            );

            return Task.FromResult(resultado);
        }
    }

    public Task<ReciboComando?> ObterReciboComandoAsync(Guid espacoTrabalhoId, string principalId, string operacao, string chaveIdempotencia, CancellationToken ct = default)
    {
        var chave = $"{espacoTrabalhoId}:{principalId}:{operacao}:{chaveIdempotencia}";
        _recibosComando.TryGetValue(chave, out var r);
        return Task.FromResult(r);
    }

    public Task SalvarReciboComandoAsync(ReciboComando recibo, CancellationToken ct = default)
    {
        var chave = $"{recibo.EspacoTrabalhoId}:{recibo.PrincipalId}:{recibo.Operacao}:{recibo.ChaveIdempotencia}";
        _recibosComando[chave] = recibo;
        return Task.CompletedTask;
    }

    public Task ReconstruirProjecoesAsync(Guid? espacoTrabalhoId = null, CancellationToken ct = default)
    {
        lock (_travaGlobal)
        {
            var topicosParaProcessar = _topicosDecisao.Values.AsEnumerable();
            if (espacoTrabalhoId.HasValue)
                topicosParaProcessar = topicosParaProcessar.Where(t => t.EspacoTrabalhoId == espacoTrabalhoId.Value);

            foreach (var topico in topicosParaProcessar.ToList())
            {
                var eventos = _eventosMemoria
                    .Where(e => e.EspacoTrabalhoId == topico.EspacoTrabalhoId && e.FluxoId == topico.TopicoId)
                    .OrderBy(e => e.VersaoFluxo)
                    .ToList();

                var reconstituido = RedutorDeDecisao.Reconstituir(topico.TopicoId, topico.EspacoTrabalhoId, topico.ProjetoId, topico.ChaveTopico, eventos, id => _cargasMemoria.TryGetValue(id, out var c) ? c : null);

                CargaMemoria? carga = null;
                if (reconstituido.RegistroAtivoId.HasValue)
                    _cargasMemoria.TryGetValue(reconstituido.RegistroAtivoId.Value, out carga);
                else if (reconstituido.PropostaPendenteId.HasValue)
                    _cargasMemoria.TryGetValue(reconstituido.PropostaPendenteId.Value, out carga);

                _decisoesAtuais[topico.TopicoId] = new DecisaoAtual(
                    topico.TopicoId,
                    topico.EspacoTrabalhoId,
                    topico.ProjetoId,
                    topico.ChaveTopico,
                    reconstituido.RegistroAtivoId,
                    reconstituido.VersaoRegistroAtivo,
                    reconstituido.VersaoFluxo,
                    reconstituido.Situacao,
                    reconstituido.PropostaPendenteId,
                    carga?.Titulo ?? string.Empty,
                    carga?.Resumo ?? string.Empty,
                    carga?.Justificativa,
                    carga?.ChavesTecnicas ?? [],
                    carga?.IncidentesRelacionadosIds ?? [],
                    carga?.Evidencias ?? [],
                    topico.AtualizadoEm
                );
            }

            var incidentesParaProcessar = _fluxosIncidentes.Values.AsEnumerable();
            if (espacoTrabalhoId.HasValue)
                incidentesParaProcessar = incidentesParaProcessar.Where(i => i.EspacoTrabalhoId == espacoTrabalhoId.Value);

            foreach (var inc in incidentesParaProcessar.ToList())
            {
                var eventos = _eventosMemoria
                    .Where(e => e.EspacoTrabalhoId == inc.EspacoTrabalhoId && e.FluxoId == inc.IncidenteId)
                    .OrderBy(e => e.VersaoFluxo)
                    .ToList();

                var reconstituido = RedutorDeIncidente.Reconstituir(inc.IncidenteId, inc.EspacoTrabalhoId, inc.ProjetoId, eventos, id => _cargasMemoria.TryGetValue(id, out var c) ? c : null);
                _cargasMemoria.TryGetValue(reconstituido.IncidenteId, out var carga);

                _incidentesAtuais[inc.IncidenteId] = new IncidenteAtual(
                    inc.IncidenteId,
                    inc.EspacoTrabalhoId,
                    inc.ProjetoId,
                    reconstituido.VersaoRegistro,
                    reconstituido.VersaoFluxo,
                    reconstituido.Situacao,
                    carga?.Titulo ?? string.Empty,
                    carga?.Resumo ?? string.Empty,
                    reconstituido.OcorridoEm,
                    carga?.ChavesTecnicas ?? [],
                    carga?.Evidencias ?? [],
                    inc.AtualizadoEm
                );
            }

            return Task.CompletedTask;
        }
    }
}
