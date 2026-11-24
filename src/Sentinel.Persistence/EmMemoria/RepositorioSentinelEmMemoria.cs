using System.Text.Json;
using Sentinel.Core.Comandos;
using Sentinel.Core.Comum;
using Sentinel.Core.Consultas;
using Sentinel.Core.Contratos;
using Sentinel.Core.Excecoes;
using Sentinel.Core.Modelos;
using Sentinel.Core.Redutores;
using Sentinel.Core.Validacao;
using Sentinel.Persistence.Comum;

namespace Sentinel.Persistence.EmMemoria;

// Repositório em memória para desenvolvimento e testes (com persistência local opcional em arquivo)
public sealed class RepositorioSentinelEmMemoria :
    IRepositorioEspacoTrabalho,
    IRepositorioDecisao,
    IRepositorioIncidente,
    IRepositorioContexto,
    IRepositorioHistorico,
    IReconstrutorProjecoes,
    IManutencaoRetencao,
    IVerificadorProntidao,
    IRepositorioUsuariosLocais,
    IRepositorioChavesApi
{
    private readonly object _trava = new();
    private readonly string? _arquivoEstado;

    private readonly Dictionary<Guid, EspacoTrabalho> _espacosTrabalho = new();
    private readonly Dictionary<Guid, Projeto> _projetos = new();
    private readonly Dictionary<string, VinculoMembro> _vinculosMembros = new();
    private readonly Dictionary<Guid, TopicoDecisao> _topicosDecisao = new();
    private readonly Dictionary<Guid, FluxoIncidente> _fluxosIncidentes = new();
    private readonly List<EventoMemoria> _eventosMemoria = [];
    private readonly Dictionary<Guid, CargaMemoria> _cargasMemoria = new();
    private readonly Dictionary<Guid, DecisaoAtual> _decisoesAtuais = new();
    private readonly Dictionary<Guid, PropostaPendente> _propostasPendentes = new();
    private readonly Dictionary<Guid, IncidenteAtual> _incidentesAtuais = new();
    private readonly Dictionary<string, ReciboComando> _recibosComando = new();
    private readonly Dictionary<Guid, ReciboConsulta> _recibosConsulta = new();
    private readonly Dictionary<string, UsuarioLocal> _usuariosLocais = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, ChaveApiAgente> _chavesApi = new();

    public RepositorioSentinelEmMemoria(string? arquivoEstado = null)
    {
        _arquivoEstado = string.IsNullOrWhiteSpace(arquivoEstado) ? null : arquivoEstado;
        if (_arquivoEstado != null)
            CarregarDoDisco();
    }

    private sealed class SnapshotLocal
    {
        public List<EspacoTrabalho> Espacos { get; set; } = [];
        public List<Projeto> Projetos { get; set; } = [];
        public List<VinculoMembro> Vinculos { get; set; } = [];
        public List<TopicoDecisao> Topicos { get; set; } = [];
        public List<FluxoIncidente> Incidentes { get; set; } = [];
        public List<EventoMemoria> Eventos { get; set; } = [];
        public List<CargaMemoria> Cargas { get; set; } = [];
        public List<ReciboComando> RecibosComando { get; set; } = [];
        public List<ReciboConsulta> RecibosConsulta { get; set; } = [];
        public List<UsuarioLocal> UsuariosLocais { get; set; } = [];
        public List<ChaveApiAgente> ChavesApi { get; set; } = [];
    }

    private void CarregarDoDisco()
    {
        if (_arquivoEstado == null || !File.Exists(_arquivoEstado))
            return;

        var json = File.ReadAllText(_arquivoEstado);
        var snap = JsonSerializer.Deserialize<SnapshotLocal>(json);
        if (snap == null)
            return;

        lock (_trava)
        {
            foreach (var e in snap.Espacos) _espacosTrabalho[e.EspacoTrabalhoId] = e;
            foreach (var p in snap.Projetos) _projetos[p.ProjetoId] = p;
            foreach (var v in snap.Vinculos) _vinculosMembros[ChaveVinculo(v.EspacoTrabalhoId, v.Emissor, v.Sujeito)] = v;
            foreach (var t in snap.Topicos) _topicosDecisao[t.TopicoId] = t;
            foreach (var i in snap.Incidentes) _fluxosIncidentes[i.IncidenteId] = i;
            _eventosMemoria.AddRange(snap.Eventos);
            foreach (var c in snap.Cargas) _cargasMemoria[c.CargaId] = c;
            foreach (var rc in snap.RecibosComando) _recibosComando[ChaveRecibo(rc.EspacoTrabalhoId, rc.PrincipalId, rc.Operacao, rc.ChaveIdempotencia)] = rc;
            foreach (var rcs in snap.RecibosConsulta) _recibosConsulta[rcs.ConsultaId] = rcs;
            foreach (var u in snap.UsuariosLocais) _usuariosLocais[u.NomeUsuario] = u;
            foreach (var k in snap.ChavesApi) _chavesApi[k.ChaveId] = k;

            ReconstruirSemTrava(null);
        }
    }

    private void PersistirSemTrava()
    {
        if (_arquivoEstado == null)
            return;

        var dir = Path.GetDirectoryName(_arquivoEstado);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var snap = new SnapshotLocal
        {
            Espacos = [.. _espacosTrabalho.Values],
            Projetos = [.. _projetos.Values],
            Vinculos = [.. _vinculosMembros.Values],
            Topicos = [.. _topicosDecisao.Values],
            Incidentes = [.. _fluxosIncidentes.Values],
            Eventos = [.. _eventosMemoria],
            Cargas = [.. _cargasMemoria.Values],
            RecibosComando = [.. _recibosComando.Values],
            RecibosConsulta = [.. _recibosConsulta.Values],
            UsuariosLocais = [.. _usuariosLocais.Values],
            ChavesApi = [.. _chavesApi.Values]
        };

        var tmp = _arquivoEstado + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snap));
        File.Move(tmp, _arquivoEstado, overwrite: true);
    }

    private static string ChaveVinculo(Guid ws, string emissor, string sujeito) => $"{ws}|{emissor}|{sujeito}";
    private static string ChaveRecibo(Guid ws, string principal, string operacao, string chave) => $"{ws}|{principal}|{operacao}|{chave}";

    public void SemearEspacoTrabalho(EspacoTrabalho espacoTrabalho)
    {
        lock (_trava) _espacosTrabalho[espacoTrabalho.EspacoTrabalhoId] = espacoTrabalho;
    }

    public void SemearProjeto(Projeto projeto)
    {
        lock (_trava) _projetos[projeto.ProjetoId] = projeto;
    }

    public void SemearVinculoMembro(VinculoMembro vinculo)
    {
        lock (_trava) _vinculosMembros[ChaveVinculo(vinculo.EspacoTrabalhoId, vinculo.Emissor, vinculo.Sujeito)] = vinculo;
    }

    public void PopularDadosDemonstracao()
    {
        lock (_trava)
        {
            var ws1 = new EspacoTrabalho(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Engenharia Central", "eng-central", Relogio.Truncar(DateTimeOffset.UtcNow.AddDays(-30)));
            if (_espacosTrabalho.ContainsKey(ws1.EspacoTrabalhoId))
                return;
            var ws2 = new EspacoTrabalho(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Segurança Isolada", "seg-isolada", Relogio.Truncar(DateTimeOffset.UtcNow.AddDays(-29)));
            _espacosTrabalho[ws1.EspacoTrabalhoId] = ws1;
            _espacosTrabalho[ws2.EspacoTrabalhoId] = ws2;

            var p1 = new Projeto(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), ws1.EspacoTrabalhoId, "Pagamentos e Cobrança", "pagamentos-cobranca", ws1.CriadoEm);
            var p2 = new Projeto(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), ws2.EspacoTrabalhoId, "Trilhas de Auditoria", "trilhas-auditoria", ws2.CriadoEm);
            _projetos[p1.ProjetoId] = p1;
            _projetos[p2.ProjetoId] = p2;

            const string emissor = "https://auth.sentinel.dev";
            foreach (var (ws, sujeito, papel) in new[]
                     {
                         (ws1.EspacoTrabalhoId, "agent-dev-1", PapelUsuario.Colaborador),
                         (ws1.EspacoTrabalhoId, "maintainer-alice", PapelUsuario.Mantenedor),
                         (ws1.EspacoTrabalhoId, "reader-bob", PapelUsuario.Leitor),
                         (ws2.EspacoTrabalhoId, "sec-agent-1", PapelUsuario.Colaborador),
                         (ws2.EspacoTrabalhoId, "sec-maintainer", PapelUsuario.Mantenedor)
                     })
            {
                _vinculosMembros[ChaveVinculo(ws, emissor, sujeito)] = new VinculoMembro(Guid.NewGuid(), ws, emissor, sujeito, papel, true, ws1.CriadoEm);
            }

            // Incidente de demonstração
            var incidenteId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
            var cargaIncidenteId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
            var quandoIncidente = Relogio.Truncar(DateTimeOffset.UtcNow.AddDays(-7));
            _fluxosIncidentes[incidenteId] = new FluxoIncidente(incidenteId, ws1.EspacoTrabalhoId, p1.ProjetoId, 1, quandoIncidente, quandoIncidente);
            _cargasMemoria[cargaIncidenteId] = new CargaMemoria(
                cargaIncidenteId, ws1.EspacoTrabalhoId, p1.ProjetoId,
                "Duplicação de cobrança em retentativas no gateway de pagamento",
                "Clientes sofreram cobrança duplicada após timeouts parciais no webhook do gateway externo.",
                null,
                ["BillingService", "WebhookController", "charge_attempts", "ERR_DUPLICATE_CHARGE"],
                [],
                [
                    new Evidencia("incident_rca", "RCA Cobrança Duplicada 2026-10", "https://example.com/rca/billing-dup-202610"),
                    new Evidencia("pull_request", "PR Correção da duplicação", "https://example.com/pull/42")
                ],
                quandoIncidente)
            { OcorridoEm = quandoIncidente };
            _eventosMemoria.Add(new EventoMemoria(Guid.NewGuid(), ws1.EspacoTrabalhoId, p1.ProjetoId, incidenteId, 1,
                TiposDeEvento.IncidenteReportado, 1, quandoIncidente, "agent-dev-1", emissor, "claude-code", cargaIncidenteId, null));

            // Decisão aprovada de demonstração
            var topicoId = Guid.Parse("c0c58a62-e095-4baf-9b56-69de5fa339ab");
            var cargaDecisaoId = Guid.Parse("ed377b6a-cfdb-4c94-a51a-d0a0f66a247a");
            var quandoProposta = Relogio.Truncar(DateTimeOffset.UtcNow.AddDays(-5));
            var quandoAprovacao = Relogio.Truncar(DateTimeOffset.UtcNow.AddDays(-4));
            _topicosDecisao[topicoId] = new TopicoDecisao(topicoId, ws1.EspacoTrabalhoId, p1.ProjetoId, "payment-idempotency", 2, quandoProposta, quandoAprovacao);
            _cargasMemoria[cargaDecisaoId] = new CargaMemoria(
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
                quandoProposta);
            var eventoProposta = new EventoMemoria(Guid.NewGuid(), ws1.EspacoTrabalhoId, p1.ProjetoId, topicoId, 1,
                TiposDeEvento.DecisaoProposta, 1, quandoProposta, "agent-dev-1", emissor, "claude-code", cargaDecisaoId, null);
            _eventosMemoria.Add(eventoProposta);
            _eventosMemoria.Add(new EventoMemoria(Guid.NewGuid(), ws1.EspacoTrabalhoId, p1.ProjetoId, topicoId, 2,
                TiposDeEvento.DecisaoAprovada, 1, quandoAprovacao, "maintainer-alice", emissor, "web-session", cargaDecisaoId, eventoProposta.EventoId));

            // Proposta pendente de demonstração
            var topicoPropostaId = Guid.Parse("f0f58a62-e095-4baf-9b56-69de5fa339cc");
            var cargaPropostaId = Guid.Parse("a1a77b6a-cfdb-4c94-a51a-d0a0f66a2411");
            var quandoPendente = Relogio.Truncar(DateTimeOffset.UtcNow.AddDays(-1));
            _topicosDecisao[topicoPropostaId] = new TopicoDecisao(topicoPropostaId, ws1.EspacoTrabalhoId, p1.ProjetoId, "payment-circuit-breaker", 1, quandoPendente, quandoPendente);
            _cargasMemoria[cargaPropostaId] = new CargaMemoria(
                cargaPropostaId, ws1.EspacoTrabalhoId, p1.ProjetoId,
                "Circuit Breaker Distribuído para Gateways de Pagamento",
                "Isolar falhas em cascata no gateway externo abrindo o circuito após 5 timeouts consecutivos em janela de 30 segundos.",
                "Evitar sobrecarga e exaustão de conexões HTTP durante instabilidades ou lentidão prolongada no provedor bancário externo.",
                ["BillingService", "CircuitBreaker", "payment-circuit-breaker", "Resilience"],
                [incidenteId],
                [
                    new Evidencia("adr", "ADR 004 Circuit Breaker de Cobranças", "https://example.com/adr/004-circuit-breaker"),
                    new Evidencia("pull_request", "PR 58 Implementação do Circuit Breaker", "https://example.com/pull/58")
                ],
                quandoPendente);
            _eventosMemoria.Add(new EventoMemoria(Guid.NewGuid(), ws1.EspacoTrabalhoId, p1.ProjetoId, topicoPropostaId, 1,
                TiposDeEvento.DecisaoProposta, 1, quandoPendente, "agent-dev-1", emissor, "claude-code", cargaPropostaId, null));

            ReconstruirSemTrava(null);
        }
    }

    public Task VerificarAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<int> ExpurgarRecibosAsync(DateTimeOffset criadosAntesDe, CancellationToken ct = default)
    {
        lock (_trava)
        {
            var removidos = 0;
            foreach (var chave in _recibosComando.Where(kv => kv.Value.CriadoEm < criadosAntesDe).Select(kv => kv.Key).ToList())
            {
                _recibosComando.Remove(chave);
                removidos++;
            }
            foreach (var chave in _recibosConsulta.Where(kv => kv.Value.CriadoEm < criadosAntesDe).Select(kv => kv.Key).ToList())
            {
                _recibosConsulta.Remove(chave);
                removidos++;
            }
            return Task.FromResult(removidos);
        }
    }

    public Task<EspacoTrabalho?> ObterEspacoTrabalhoAsync(Guid espacoTrabalhoId, CancellationToken ct = default)
    {
        lock (_trava) return Task.FromResult(_espacosTrabalho.GetValueOrDefault(espacoTrabalhoId));
    }

    public Task<List<EspacoTrabalho>> ObterEspacosDaIdentidadeAsync(string emissor, string sujeito, CancellationToken ct = default)
    {
        lock (_trava)
        {
            var ids = _vinculosMembros.Values
                .Where(v => v.Ativo && v.Emissor == emissor && v.Sujeito == sujeito)
                .Select(v => v.EspacoTrabalhoId)
                .ToHashSet();
            var lista = _espacosTrabalho.Values
                .Where(e => ids.Contains(e.EspacoTrabalhoId))
                .OrderBy(e => e.CriadoEm).ThenBy(e => e.EspacoTrabalhoId)
                .ToList();
            return Task.FromResult(lista);
        }
    }

    public Task<VinculoMembro?> ObterVinculoMembroAsync(Guid espacoTrabalhoId, string emissor, string sujeito, CancellationToken ct = default)
    {
        lock (_trava) return Task.FromResult(_vinculosMembros.GetValueOrDefault(ChaveVinculo(espacoTrabalhoId, emissor, sujeito)));
    }

    public Task<Projeto?> ObterProjetoAsync(Guid espacoTrabalhoId, Guid projetoId, CancellationToken ct = default)
    {
        lock (_trava)
        {
            return Task.FromResult(_projetos.TryGetValue(projetoId, out var p) && p.EspacoTrabalhoId == espacoTrabalhoId ? p : null);
        }
    }

    public Task<List<Projeto>> ObterProjetosPorEspacoTrabalhoAsync(Guid espacoTrabalhoId, CancellationToken ct = default)
    {
        lock (_trava)
        {
            return Task.FromResult(_projetos.Values.Where(p => p.EspacoTrabalhoId == espacoTrabalhoId).OrderBy(p => p.Nome).ToList());
        }
    }

    public Task<bool> PossuiAlgumEspacoAsync(CancellationToken ct = default)
    {
        lock (_trava) return Task.FromResult(_espacosTrabalho.Count > 0);
    }

    public Task<EspacoTrabalho> CriarEspacoTrabalhoAsync(string nome, string slug, Guid? espacoTrabalhoId = null, CancellationToken ct = default)
    {
        lock (_trava)
        {
            if (_espacosTrabalho.Values.Any(e => string.Equals(e.IdentificadorSlug, slug, StringComparison.OrdinalIgnoreCase)))
                throw new ExcecaoDeConflito($"Workspace com slug '{slug}' já existe.");

            var ws = new EspacoTrabalho(espacoTrabalhoId ?? Guid.NewGuid(), nome, slug, Relogio.Truncar(DateTimeOffset.UtcNow));
            _espacosTrabalho[ws.EspacoTrabalhoId] = ws;
            PersistirSemTrava();
            return Task.FromResult(ws);
        }
    }

    public Task<List<EspacoTrabalho>> ListarTodosEspacosAsync(CancellationToken ct = default)
    {
        lock (_trava)
        {
            return Task.FromResult(_espacosTrabalho.Values.OrderBy(e => e.CriadoEm).ThenBy(e => e.EspacoTrabalhoId).ToList());
        }
    }

    public Task<Projeto> CriarProjetoAsync(Guid espacoTrabalhoId, string nome, string slug, Guid? projetoId = null, CancellationToken ct = default)
    {
        lock (_trava)
        {
            if (!_espacosTrabalho.ContainsKey(espacoTrabalhoId))
                throw new ExcecaoDeNaoEncontrado($"Workspace {espacoTrabalhoId} não encontrado.");

            if (_projetos.Values.Any(p => p.EspacoTrabalhoId == espacoTrabalhoId && string.Equals(p.IdentificadorSlug, slug, StringComparison.OrdinalIgnoreCase)))
                throw new ExcecaoDeConflito($"Projeto com slug '{slug}' já existe no workspace.");

            var proj = new Projeto(projetoId ?? Guid.NewGuid(), espacoTrabalhoId, nome, slug, Relogio.Truncar(DateTimeOffset.UtcNow));
            _projetos[proj.ProjetoId] = proj;
            PersistirSemTrava();
            return Task.FromResult(proj);
        }
    }

    public Task<bool> ProjetoPossuiHistoricoAsync(Guid espacoTrabalhoId, Guid projetoId, CancellationToken ct = default)
    {
        lock (_trava)
        {
            var temHistorico = _topicosDecisao.Values.Any(t => t.EspacoTrabalhoId == espacoTrabalhoId && t.ProjetoId == projetoId)
                || _fluxosIncidentes.Values.Any(f => f.EspacoTrabalhoId == espacoTrabalhoId && f.ProjetoId == projetoId)
                || _eventosMemoria.Any(e => e.EspacoTrabalhoId == espacoTrabalhoId && e.ProjetoId == projetoId);
            return Task.FromResult(temHistorico);
        }
    }

    public Task<bool> RemoverProjetoAsync(Guid espacoTrabalhoId, Guid projetoId, CancellationToken ct = default)
    {
        lock (_trava)
        {
            if (!_projetos.TryGetValue(projetoId, out var p) || p.EspacoTrabalhoId != espacoTrabalhoId)
                return Task.FromResult(false);

            _projetos.Remove(projetoId);
            PersistirSemTrava();
            return Task.FromResult(true);
        }
    }

    public Task<VinculoMembro> CriarOuAtualizarVinculoAsync(Guid espacoTrabalhoId, string emissor, string sujeito, PapelUsuario papel, bool ativo = true, CancellationToken ct = default)
    {
        lock (_trava)
        {
            if (!_espacosTrabalho.ContainsKey(espacoTrabalhoId))
                throw new ExcecaoDeNaoEncontrado($"Workspace {espacoTrabalhoId} não encontrado.");

            var chave = ChaveVinculo(espacoTrabalhoId, emissor, sujeito);
            var id = _vinculosMembros.TryGetValue(chave, out var existente) ? existente.VinculoId : Guid.NewGuid();
            var criadoEm = existente?.CriadoEm ?? Relogio.Truncar(DateTimeOffset.UtcNow);
            var vinculo = new VinculoMembro(id, espacoTrabalhoId, emissor, sujeito, papel, ativo, criadoEm);
            _vinculosMembros[chave] = vinculo;
            PersistirSemTrava();
            return Task.FromResult(vinculo);
        }
    }

    public Task<List<VinculoMembro>> ListarVinculosDoEspacoAsync(Guid espacoTrabalhoId, CancellationToken ct = default)
    {
        lock (_trava)
        {
            return Task.FromResult(_vinculosMembros.Values
                .Where(v => v.EspacoTrabalhoId == espacoTrabalhoId)
                .OrderBy(v => v.CriadoEm)
                .ToList());
        }
    }

    public Task<bool> RemoverVinculoAsync(Guid espacoTrabalhoId, Guid vinculoId, CancellationToken ct = default)
    {
        lock (_trava)
        {
            var encontrado = _vinculosMembros.FirstOrDefault(kv => kv.Value.EspacoTrabalhoId == espacoTrabalhoId && kv.Value.VinculoId == vinculoId);
            if (encontrado.Key == null)
                return Task.FromResult(false);

            _vinculosMembros.Remove(encontrado.Key);
            PersistirSemTrava();
            return Task.FromResult(true);
        }
    }

    public Task<TopicoDecisao?> ObterTopicoPorChaveAsync(Guid espacoTrabalhoId, Guid projetoId, string chaveTopico, CancellationToken ct = default)
    {
        lock (_trava) return Task.FromResult(BuscarTopicoPorChave(espacoTrabalhoId, projetoId, chaveTopico));
    }

    private TopicoDecisao? BuscarTopicoPorChave(Guid ws, Guid proj, string chave) =>
        _topicosDecisao.Values.FirstOrDefault(t =>
            t.EspacoTrabalhoId == ws && t.ProjetoId == proj &&
            string.Equals(t.ChaveTopico, chave, StringComparison.OrdinalIgnoreCase));

    public Task<TopicoDecisao?> ObterTopicoPorIdAsync(Guid espacoTrabalhoId, Guid projetoId, Guid topicoId, CancellationToken ct = default)
    {
        lock (_trava)
        {
            return Task.FromResult(_topicosDecisao.TryGetValue(topicoId, out var t) && t.EspacoTrabalhoId == espacoTrabalhoId && t.ProjetoId == projetoId ? t : null);
        }
    }

    public Task<DecisaoAtual?> ObterDecisaoAtualAsync(Guid espacoTrabalhoId, Guid projetoId, Guid topicoId, CancellationToken ct = default)
    {
        lock (_trava)
        {
            return Task.FromResult(_decisoesAtuais.TryGetValue(topicoId, out var d) && d.EspacoTrabalhoId == espacoTrabalhoId && d.ProjetoId == projetoId ? d : null);
        }
    }

    public Task<List<PropostaPendente>> ObterPropostasPendentesAsync(Guid espacoTrabalhoId, Guid? projetoId = null, CancellationToken ct = default)
    {
        lock (_trava)
        {
            var lista = _propostasPendentes.Values
                .Where(p => p.EspacoTrabalhoId == espacoTrabalhoId && (!projetoId.HasValue || p.ProjetoId == projetoId.Value))
                .OrderByDescending(p => p.PropostaEm).ThenBy(p => p.TopicoId)
                .ToList();
            return Task.FromResult(lista);
        }
    }

    public Task<List<DecisaoAtual>> ObterDecisoesAsync(Guid espacoTrabalhoId, Guid? projetoId = null, CancellationToken ct = default)
    {
        lock (_trava)
        {
            var lista = _decisoesAtuais.Values
                .Where(d => d.EspacoTrabalhoId == espacoTrabalhoId && (!projetoId.HasValue || d.ProjetoId == projetoId.Value))
                .OrderByDescending(d => d.AtualizadoEm).ThenBy(d => d.TopicoId)
                .ToList();
            return Task.FromResult(lista);
        }
    }

    public async Task<List<DecisaoAtual>> ObterDecisoesVigentesAsync(Guid espacoTrabalhoId, Guid? projetoId = null, CancellationToken ct = default)
    {
        var todas = await ObterDecisoesAsync(espacoTrabalhoId, projetoId, ct);
        return todas.Where(d => d.Situacao == StatusDecisao.Ativa && d.RegistroAtivoId != null).ToList();
    }

    public Task<List<EventoMemoria>> ObterEventosRecentesAsync(Guid espacoTrabalhoId, int limite = 50, CancellationToken ct = default)
    {
        lock (_trava)
        {
            var eventos = _eventosMemoria
                .Where(e => e.EspacoTrabalhoId == espacoTrabalhoId)
                .OrderByDescending(e => e.RegistradoEm).ThenByDescending(e => e.VersaoFluxo)
                .Take(limite)
                .ToList();
            return Task.FromResult(eventos);
        }
    }

    public Task<HistoricoDoFluxo> ObterHistoricoDoFluxoAsync(Guid espacoTrabalhoId, Guid fluxoId, CancellationToken ct = default)
    {
        lock (_trava)
        {
            var eventos = EventosDoFluxo(espacoTrabalhoId, fluxoId);
            var cargas = eventos
                .Where(e => e.CargaId.HasValue && _cargasMemoria.ContainsKey(e.CargaId.Value))
                .Select(e => _cargasMemoria[e.CargaId!.Value])
                .Where(c => c.EspacoTrabalhoId == espacoTrabalhoId)
                .DistinctBy(c => c.CargaId)
                .ToDictionary(c => c.CargaId);
            return Task.FromResult(new HistoricoDoFluxo(eventos, cargas));
        }
    }

    private List<EventoMemoria> EventosDoFluxo(Guid ws, Guid fluxoId) =>
        _eventosMemoria
            .Where(e => e.EspacoTrabalhoId == ws && e.FluxoId == fluxoId)
            .OrderBy(e => e.VersaoFluxo)
            .ToList();

    private CargaMemoria? ResolverCarga(Guid ws, Guid cargaId) =>
        _cargasMemoria.TryGetValue(cargaId, out var c) && c.EspacoTrabalhoId == ws ? c : null;

    private T ExecutarIdempotente<T>(Guid ws, string principal, string operacao, string chave, string hash, Func<DateTimeOffset, T> acao)
    {
        var chaveRecibo = ChaveRecibo(ws, principal, operacao, chave);
        if (_recibosComando.TryGetValue(chaveRecibo, out var existente))
        {
            if (existente.HashArgumentos != hash)
                throw new ExcecaoDeChaveIdempotenciaReutilizada("A chave de idempotência foi reutilizada com argumentos distintos.");
            return JsonSerializer.Deserialize<T>(existente.ResultadoSerializado)!;
        }

        var agora = Relogio.AgoraUtc();
        var resultado = acao(agora);
        _recibosComando[chaveRecibo] = new ReciboComando(Guid.NewGuid(), ws, principal, operacao, chave, hash, JsonSerializer.Serialize(resultado), agora);
        PersistirSemTrava();
        return resultado;
    }

    private TopicoDecisao TravarTopico(Guid ws, Guid proj, Guid topicoId, int versaoEsperada)
    {
        if (!_topicosDecisao.TryGetValue(topicoId, out var topico) || topico.EspacoTrabalhoId != ws || topico.ProjetoId != proj)
            throw new ExcecaoDeNaoEncontrado("Tópico de decisão não encontrado.");
        if (topico.VersaoAtual != versaoEsperada)
            throw new ExcecaoDeConflitoDeVersao(topicoId, versaoEsperada, topico.VersaoAtual);
        return topico;
    }

    private void AnexarEventoDecisao(TopicoDecisao topico, EventoMemoria evento, DateTimeOffset agora)
    {
        _eventosMemoria.Add(evento);
        _topicosDecisao[topico.TopicoId] = topico with { VersaoAtual = evento.VersaoFluxo, AtualizadoEm = agora };
        AtualizarProjecaoDecisao(_topicosDecisao[topico.TopicoId]);
    }

    private void AtualizarProjecaoDecisao(TopicoDecisao topico)
    {
        var eventos = EventosDoFluxo(topico.EspacoTrabalhoId, topico.TopicoId);
        if (eventos.Count == 0)
        {
            _decisoesAtuais.Remove(topico.TopicoId);
            _propostasPendentes.Remove(topico.TopicoId);
            return;
        }

        var estado = RedutorDeDecisao.Reconstituir(topico.TopicoId, topico.EspacoTrabalhoId, topico.ProjetoId, topico.ChaveTopico,
            eventos, id => ResolverCarga(topico.EspacoTrabalhoId, id));
        var (atual, pendente) = RedutorDeDecisao.Projetar(estado, eventos[^1].RegistradoEm);
        _decisoesAtuais[topico.TopicoId] = atual;
        if (pendente != null) _propostasPendentes[topico.TopicoId] = pendente;
        else _propostasPendentes.Remove(topico.TopicoId);
    }

    public Task<ResultadoProporDecisao> ProporDecisaoAsync(ComandoProporDecisao comando, CancellationToken ct = default)
    {
        var hash = AjudanteIdempotencia.HashProporDecisao(comando);
        lock (_trava)
        {
            var resultado = ExecutarIdempotente(comando.EspacoTrabalhoId, comando.AutorSujeito, "propose_decision", comando.ChaveIdempotencia, hash, agora =>
            {
                var topico = BuscarTopicoPorChave(comando.EspacoTrabalhoId, comando.ProjetoId, comando.ChaveTopico);
                var versaoAtual = topico?.VersaoAtual ?? 0;
                if (comando.VersaoEsperada != versaoAtual)
                    throw new ExcecaoDeConflitoDeVersao(topico?.TopicoId, comando.VersaoEsperada, versaoAtual);

                if (topico != null && _propostasPendentes.TryGetValue(topico.TopicoId, out var pendente))
                    throw new ExcecaoDePropostaPendente(topico.TopicoId, pendente.PropostaId);

                topico ??= new TopicoDecisao(Guid.NewGuid(), comando.EspacoTrabalhoId, comando.ProjetoId, comando.ChaveTopico, 0, agora, agora);

                var cargaId = Guid.NewGuid();
                _cargasMemoria[cargaId] = new CargaMemoria(cargaId, comando.EspacoTrabalhoId, comando.ProjetoId,
                    comando.Titulo, comando.Resumo, comando.Justificativa,
                    comando.ChavesTecnicas ?? [], comando.IncidentesRelacionadosIds ?? [], comando.Evidencias, agora);

                var novaVersao = versaoAtual + 1;
                AnexarEventoDecisao(topico, new EventoMemoria(Guid.NewGuid(), comando.EspacoTrabalhoId, comando.ProjetoId,
                    topico.TopicoId, novaVersao, TiposDeEvento.DecisaoProposta, 1, agora,
                    comando.AutorSujeito, comando.AutorEmissor, comando.ClienteId, cargaId, null), agora);

                return new ResultadoProporDecisao(topico.TopicoId, cargaId, novaVersao, "proposed");
            });
            return Task.FromResult(resultado);
        }
    }

    public Task<ResultadoAprovarDecisao> AprovarDecisaoAsync(ComandoAprovarDecisao comando, CancellationToken ct = default)
    {
        var hash = AjudanteIdempotencia.HashAprovarDecisao(comando);
        lock (_trava)
        {
            var resultado = ExecutarIdempotente(comando.EspacoTrabalhoId, comando.RevisorSujeito, "approve_decision", comando.ChaveIdempotencia, hash, agora =>
            {
                var topico = TravarTopico(comando.EspacoTrabalhoId, comando.ProjetoId, comando.TopicoId, comando.VersaoEsperada);
                if (!_propostasPendentes.TryGetValue(topico.TopicoId, out var pendente) || pendente.PropostaId != comando.PropostaId)
                    throw new ExcecaoDeValidacao("Não existe proposta pendente com o ID informado para aprovação.");

                var eventoProposta = _eventosMemoria.Last(e => e.FluxoId == topico.TopicoId && e.CargaId == comando.PropostaId && e.Tipo == TiposDeEvento.DecisaoProposta);
                var novaVersao = topico.VersaoAtual + 1;
                AnexarEventoDecisao(topico, new EventoMemoria(Guid.NewGuid(), comando.EspacoTrabalhoId, comando.ProjetoId,
                    topico.TopicoId, novaVersao, TiposDeEvento.DecisaoAprovada, 1, agora,
                    comando.RevisorSujeito, comando.RevisorEmissor, "web-session", comando.PropostaId, eventoProposta.EventoId), agora);

                return new ResultadoAprovarDecisao(topico.TopicoId, comando.PropostaId, novaVersao, "active");
            });
            return Task.FromResult(resultado);
        }
    }

    public Task<ResultadoRejeitarDecisao> RejeitarDecisaoAsync(ComandoRejeitarDecisao comando, CancellationToken ct = default)
    {
        var hash = AjudanteIdempotencia.HashRejeitarDecisao(comando);
        lock (_trava)
        {
            var resultado = ExecutarIdempotente(comando.EspacoTrabalhoId, comando.RevisorSujeito, "reject_decision", comando.ChaveIdempotencia, hash, agora =>
            {
                var topico = TravarTopico(comando.EspacoTrabalhoId, comando.ProjetoId, comando.TopicoId, comando.VersaoEsperada);
                if (!_propostasPendentes.TryGetValue(topico.TopicoId, out var pendente) || pendente.PropostaId != comando.PropostaId)
                    throw new ExcecaoDeValidacao("Não existe proposta pendente com o ID informado para rejeição.");

                var eventoProposta = _eventosMemoria.Last(e => e.FluxoId == topico.TopicoId && e.CargaId == comando.PropostaId && e.Tipo == TiposDeEvento.DecisaoProposta);
                var novaVersao = topico.VersaoAtual + 1;
                AnexarEventoDecisao(topico, new EventoMemoria(Guid.NewGuid(), comando.EspacoTrabalhoId, comando.ProjetoId,
                    topico.TopicoId, novaVersao, TiposDeEvento.DecisaoRejeitada, 1, agora,
                    comando.RevisorSujeito, comando.RevisorEmissor, "web-session", comando.PropostaId, eventoProposta.EventoId)
                { Motivo = comando.Motivo }, agora);

                var situacao = _decisoesAtuais[topico.TopicoId].Situacao.ParaTexto();
                return new ResultadoRejeitarDecisao(topico.TopicoId, novaVersao, situacao);
            });
            return Task.FromResult(resultado);
        }
    }

    public Task<ResultadoRestaurarDecisao> RestaurarDecisaoAsync(ComandoRestaurarDecisao comando, CancellationToken ct = default)
    {
        var hash = AjudanteIdempotencia.HashRestaurarDecisao(comando);
        lock (_trava)
        {
            var resultado = ExecutarIdempotente(comando.EspacoTrabalhoId, comando.RevisorSujeito, "restore_decision", comando.ChaveIdempotencia, hash, agora =>
            {
                var topico = TravarTopico(comando.EspacoTrabalhoId, comando.ProjetoId, comando.TopicoId, comando.VersaoEsperada);
                if (_propostasPendentes.TryGetValue(topico.TopicoId, out var pendente))
                    throw new ExcecaoDePropostaPendente(topico.TopicoId, pendente.PropostaId);
                if (comando.VersaoParaRestaurar > topico.VersaoAtual)
                    throw new ExcecaoDeValidacao("A versão a restaurar não existe no histórico do tópico.");

                var eventos = EventosDoFluxo(topico.EspacoTrabalhoId, topico.TopicoId);
                var snapshot = RedutorDeDecisao.Reconstituir(topico.TopicoId, topico.EspacoTrabalhoId, topico.ProjetoId, topico.ChaveTopico,
                    eventos, id => ResolverCarga(topico.EspacoTrabalhoId, id), comando.VersaoParaRestaurar);
                if (snapshot.RegistroAtivoId == null)
                    throw new ExcecaoDeValidacao("A versão indicada não possui decisão ativa para restauração.");
                var original = ResolverCarga(topico.EspacoTrabalhoId, snapshot.RegistroAtivoId.Value)
                    ?? throw new ExcecaoDeNaoEncontrado("Carga histórica da decisão não encontrada.");

                var novaCargaId = Guid.NewGuid();
                _cargasMemoria[novaCargaId] = original with { CargaId = novaCargaId, CriadoEm = agora };
                var novaVersao = topico.VersaoAtual + 1;
                AnexarEventoDecisao(topico, new EventoMemoria(Guid.NewGuid(), comando.EspacoTrabalhoId, comando.ProjetoId,
                    topico.TopicoId, novaVersao, TiposDeEvento.DecisaoProposta, 1, agora,
                    comando.RevisorSujeito, comando.RevisorEmissor, "web-session", novaCargaId, null), agora);

                return new ResultadoRestaurarDecisao(topico.TopicoId, novaCargaId, novaVersao, "proposed");
            });
            return Task.FromResult(resultado);
        }
    }

    public Task<FluxoIncidente?> ObterIncidentePorIdAsync(Guid espacoTrabalhoId, Guid projetoId, Guid incidenteId, CancellationToken ct = default)
    {
        lock (_trava)
        {
            return Task.FromResult(_fluxosIncidentes.TryGetValue(incidenteId, out var i) && i.EspacoTrabalhoId == espacoTrabalhoId && i.ProjetoId == projetoId ? i : null);
        }
    }

    public Task<IncidenteAtual?> ObterIncidenteAtualAsync(Guid espacoTrabalhoId, Guid projetoId, Guid incidenteId, CancellationToken ct = default)
    {
        lock (_trava)
        {
            return Task.FromResult(_incidentesAtuais.TryGetValue(incidenteId, out var i) && i.EspacoTrabalhoId == espacoTrabalhoId && i.ProjetoId == projetoId ? i : null);
        }
    }

    public Task<List<IncidenteAtual>> ObterIncidentesAsync(Guid espacoTrabalhoId, Guid? projetoId = null, CancellationToken ct = default)
    {
        lock (_trava)
        {
            var lista = _incidentesAtuais.Values
                .Where(i => i.EspacoTrabalhoId == espacoTrabalhoId && (!projetoId.HasValue || i.ProjetoId == projetoId.Value))
                .OrderByDescending(i => i.OcorridoEm).ThenBy(i => i.IncidenteId)
                .ToList();
            return Task.FromResult(lista);
        }
    }

    public Task<ResultadoRegistrarIncidente> RegistrarIncidenteAsync(ComandoRegistrarIncidente comando, CancellationToken ct = default)
    {
        var hash = AjudanteIdempotencia.HashRegistrarIncidente(comando);
        lock (_trava)
        {
            var resultado = ExecutarIdempotente(comando.EspacoTrabalhoId, comando.AutorSujeito, "log_incident", comando.ChaveIdempotencia, hash, agora =>
            {
                var incidenteId = Guid.NewGuid();
                var cargaId = Guid.NewGuid();
                var ocorridoEm = Relogio.Truncar(comando.OcorridoEm ?? agora);

                var fluxo = new FluxoIncidente(incidenteId, comando.EspacoTrabalhoId, comando.ProjetoId, 1, agora, agora);
                _fluxosIncidentes[incidenteId] = fluxo;
                _cargasMemoria[cargaId] = new CargaMemoria(cargaId, comando.EspacoTrabalhoId, comando.ProjetoId,
                    comando.Titulo, comando.Resumo, null, comando.ChavesTecnicas ?? [], [], comando.Evidencias, agora)
                { OcorridoEm = ocorridoEm };
                _eventosMemoria.Add(new EventoMemoria(Guid.NewGuid(), comando.EspacoTrabalhoId, comando.ProjetoId,
                    incidenteId, 1, TiposDeEvento.IncidenteReportado, 1, agora,
                    comando.AutorSujeito, comando.AutorEmissor, comando.ClienteId, cargaId, null));
                AtualizarProjecaoIncidente(fluxo);

                return new ResultadoRegistrarIncidente(incidenteId, 1, "reported");
            });
            return Task.FromResult(resultado);
        }
    }

    private void AtualizarProjecaoIncidente(FluxoIncidente fluxo)
    {
        var eventos = EventosDoFluxo(fluxo.EspacoTrabalhoId, fluxo.IncidenteId);
        if (eventos.Count == 0)
        {
            _incidentesAtuais.Remove(fluxo.IncidenteId);
            return;
        }
        var estado = RedutorDeIncidente.Reconstituir(fluxo.IncidenteId, fluxo.EspacoTrabalhoId, fluxo.ProjetoId, eventos,
            id => ResolverCarga(fluxo.EspacoTrabalhoId, id));
        _incidentesAtuais[fluxo.IncidenteId] = RedutorDeIncidente.Projetar(estado, fluxo.CriadoEm);
    }

    public Task<ResultadoConsultaContexto> ConsultarContextoAsync(ConsultaContexto consulta, CancellationToken ct = default)
    {
        lock (_trava)
        {
            var consultaId = Guid.NewGuid();
            var agora = Relogio.AgoraUtc();
            var termos = consulta.TermoBusca.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var termosNormalizados = termos.Append(consulta.TermoBusca.Trim())
                .Select(ValidadorDeInvariantes.NormalizarChaveTecnica).ToHashSet();

            var candidatos = new List<(ItemContexto Item, bool Exata, int Pontuacao, DateTimeOffset Atualizado)>();

            if (consulta.TipoRegistro is "decision" or "both")
            {
                foreach (var d in _decisoesAtuais.Values.Where(d =>
                             d.EspacoTrabalhoId == consulta.EspacoTrabalhoId && d.ProjetoId == consulta.ProjetoId &&
                             d.Situacao == StatusDecisao.Ativa && d.RegistroAtivoId.HasValue))
                {
                    var exata = d.ChavesTecnicas.Any(k => termosNormalizados.Contains(ValidadorDeInvariantes.NormalizarChaveTecnica(k)));
                    var pontos = CalcularPontuacaoBusca(d.Titulo, d.Resumo, d.Justificativa, d.ChavesTecnicas, termos);
                    if (exata || pontos > 0)
                    {
                        candidatos.Add((new ItemContexto("decision", d.TopicoId, null, d.RegistroAtivoId!.Value,
                            d.VersaoRegistroAtivo ?? 1, d.VersaoFluxo, "active", d.Titulo, d.Resumo, d.Evidencias), exata, pontos, d.AtualizadoEm));
                    }
                }
            }

            if (consulta.TipoRegistro is "incident" or "both")
            {
                foreach (var i in _incidentesAtuais.Values.Where(i =>
                             i.EspacoTrabalhoId == consulta.EspacoTrabalhoId && i.ProjetoId == consulta.ProjetoId &&
                             i.Situacao == StatusIncidente.Reportado))
                {
                    var exata = i.ChavesTecnicas.Any(k => termosNormalizados.Contains(ValidadorDeInvariantes.NormalizarChaveTecnica(k)));
                    var pontos = CalcularPontuacaoBusca(i.Titulo, i.Resumo, null, i.ChavesTecnicas, termos);
                    if (exata || pontos > 0)
                    {
                        candidatos.Add((new ItemContexto("incident", null, i.IncidenteId, i.IncidenteId,
                            i.VersaoRegistro, i.VersaoFluxo, "reported", i.Titulo, i.Resumo, i.Evidencias), exata, pontos, i.CriadoEm));
                    }
                }
            }

            // Ordenação determinística por relevância e data
            var itens = candidatos
                .OrderByDescending(c => c.Exata)
                .ThenByDescending(c => c.Pontuacao)
                .ThenByDescending(c => c.Atualizado)
                .ThenBy(c => c.Item.RegistroId)
                .Take(consulta.Limite)
                .Select(c => c.Item)
                .ToList();

            var itensRecibo = itens.Select((item, idx) => new ItemReciboConsulta(
                Guid.NewGuid(), consultaId, consulta.EspacoTrabalhoId, idx + 1, item.TipoRegistro,
                item.TopicoId ?? item.IncidenteId!.Value, item.RegistroId, item.VersaoRegistro, item.VersaoFluxo, item.Situacao)).ToList();

            var digest = DigestDeComprovante.Calcular(consultaId, consulta.EspacoTrabalhoId, consulta.ProjetoId,
                consulta.PrincipalId, consulta.PrincipalEmissor, consulta.ClienteId, "textual", agora, itensRecibo);

            _recibosConsulta[consultaId] = new ReciboConsulta(consultaId, consulta.EspacoTrabalhoId, consulta.ProjetoId,
                consulta.PrincipalId, consulta.PrincipalEmissor, consulta.ClienteId, "textual", agora, itensRecibo, digest);
            PersistirSemTrava();

            return Task.FromResult(new ResultadoConsultaContexto(consultaId, itens.Count > 0 ? "found" : "none", "textual", itens));
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

    public Task<ResultadoComprovanteContexto?> ObterComprovanteContextoAsync(Guid espacoTrabalhoId, Guid projetoId, Guid consultaId, CancellationToken ct = default)
    {
        lock (_trava)
        {
            if (!_recibosConsulta.TryGetValue(consultaId, out var r) || r.EspacoTrabalhoId != espacoTrabalhoId || r.ProjetoId != projetoId)
                return Task.FromResult<ResultadoComprovanteContexto?>(null);

            var recalculado = DigestDeComprovante.Calcular(r.ConsultaId, r.EspacoTrabalhoId, r.ProjetoId, r.PrincipalId,
                r.PrincipalEmissor, r.ClienteId, r.ModoBusca, r.CriadoEm, r.Itens);
            return Task.FromResult<ResultadoComprovanteContexto?>(new ResultadoComprovanteContexto(
                r.ConsultaId, r.EspacoTrabalhoId, r.ProjetoId, r.PrincipalId, r.PrincipalEmissor, r.ClienteId,
                r.ModoBusca, r.CriadoEm, r.Itens.ToList(), r.Digest, recalculado == r.Digest));
        }
    }

    public Task ReconstruirProjecoesAsync(Guid? espacoTrabalhoId = null, CancellationToken ct = default)
    {
        lock (_trava)
        {
            ReconstruirSemTrava(espacoTrabalhoId);
            return Task.CompletedTask;
        }
    }

    private void ReconstruirSemTrava(Guid? espacoTrabalhoId)
    {
        bool Alvo(Guid ws) => !espacoTrabalhoId.HasValue || ws == espacoTrabalhoId.Value;

        foreach (var id in _decisoesAtuais.Values.Where(d => Alvo(d.EspacoTrabalhoId)).Select(d => d.TopicoId).ToList())
            _decisoesAtuais.Remove(id);
        foreach (var id in _propostasPendentes.Values.Where(p => Alvo(p.EspacoTrabalhoId)).Select(p => p.TopicoId).ToList())
            _propostasPendentes.Remove(id);
        foreach (var id in _incidentesAtuais.Values.Where(i => Alvo(i.EspacoTrabalhoId)).Select(i => i.IncidenteId).ToList())
            _incidentesAtuais.Remove(id);

        foreach (var topico in _topicosDecisao.Values.Where(t => Alvo(t.EspacoTrabalhoId)).ToList())
        {
            var eventos = EventosDoFluxo(topico.EspacoTrabalhoId, topico.TopicoId);
            var versao = eventos.Count == 0 ? 0 : eventos[^1].VersaoFluxo;
            _topicosDecisao[topico.TopicoId] = topico with { VersaoAtual = versao };
            AtualizarProjecaoDecisao(_topicosDecisao[topico.TopicoId]);
        }

        foreach (var fluxo in _fluxosIncidentes.Values.Where(f => Alvo(f.EspacoTrabalhoId)).ToList())
            AtualizarProjecaoIncidente(fluxo);
    }

    // Usuários locais
    public Task<bool> PossuiAlgumUsuarioAsync(CancellationToken ct = default)
    {
        lock (_trava) return Task.FromResult(_usuariosLocais.Count > 0);
    }

    public Task<UsuarioLocal?> ObterPorNomeUsuarioAsync(string nomeUsuario, CancellationToken ct = default)
    {
        lock (_trava) return Task.FromResult(_usuariosLocais.GetValueOrDefault(nomeUsuario.Trim().ToLowerInvariant()));
    }

    public Task<List<UsuarioLocal>> ListarUsuariosDoEspacoAsync(Guid espacoTrabalhoId, CancellationToken ct = default)
    {
        lock (_trava)
        {
            var vinculos = _vinculosMembros.Values
                .Where(v => v.EspacoTrabalhoId == espacoTrabalhoId)
                .Select(v => (v.Emissor, v.Sujeito))
                .ToHashSet();

            var lista = _usuariosLocais.Values
                .Where(u => vinculos.Contains((u.Emissor, u.Sujeito)))
                .OrderBy(u => u.CriadoEm)
                .ToList();
            return Task.FromResult(lista);
        }
    }

    public Task<UsuarioLocal> CriarUsuarioLocalAsync(UsuarioLocal usuario, CancellationToken ct = default)
    {
        lock (_trava)
        {
            if (_usuariosLocais.ContainsKey(usuario.NomeUsuario))
                throw new ExcecaoDeConflito($"Usuário local '{usuario.NomeUsuario}' já existe.");
            _usuariosLocais[usuario.NomeUsuario] = usuario;
            PersistirSemTrava();
            return Task.FromResult(usuario);
        }
    }

    // Chaves de API para agentes MCP
    public Task<ChaveApiAgente> CriarChaveApiAsync(ChaveApiAgente chave, CancellationToken ct = default)
    {
        lock (_trava)
        {
            if (_chavesApi.Values.Any(k => k.HashChave == chave.HashChave))
                throw new ExcecaoDeConflito("Conflito ao gerar hash da chave de API.");
            _chavesApi[chave.ChaveId] = chave;
            PersistirSemTrava();
            return Task.FromResult(chave);
        }
    }

    public Task<List<ChaveApiAgente>> ListarChavesApiAsync(Guid espacoTrabalhoId, CancellationToken ct = default)
    {
        lock (_trava)
        {
            var lista = _chavesApi.Values
                .Where(k => k.EspacoTrabalhoId == espacoTrabalhoId)
                .OrderByDescending(k => k.CriadoEm)
                .ToList();
            return Task.FromResult(lista);
        }
    }

    public Task<ChaveApiAgente?> ObterChaveApiPorHashAsync(string hashSha256Hex, CancellationToken ct = default)
    {
        lock (_trava)
        {
            var encontrada = _chavesApi.Values.FirstOrDefault(k => string.Equals(k.HashChave, hashSha256Hex, StringComparison.Ordinal));
            return Task.FromResult(encontrada);
        }
    }

    public Task RegistrarUsoChaveApiAsync(Guid espacoTrabalhoId, Guid chaveId, DateTimeOffset usadoEm, CancellationToken ct = default)
    {
        lock (_trava)
        {
            if (_chavesApi.TryGetValue(chaveId, out var k) && k.EspacoTrabalhoId == espacoTrabalhoId)
            {
                var ultimoPersistido = k.UltimoUsoEm;
                _chavesApi[chaveId] = k with { UltimoUsoEm = usadoEm };
                if (!ultimoPersistido.HasValue || (usadoEm - ultimoPersistido.Value).TotalSeconds >= 60)
                    PersistirSemTrava();
            }
            return Task.CompletedTask;
        }
    }

    public Task<bool> RevogarChaveApiAsync(Guid espacoTrabalhoId, Guid chaveId, CancellationToken ct = default)
    {
        lock (_trava)
        {
            if (!_chavesApi.TryGetValue(chaveId, out var k) || k.EspacoTrabalhoId != espacoTrabalhoId)
                return Task.FromResult(false);
            _chavesApi.Remove(chaveId);
            PersistirSemTrava();
            return Task.FromResult(true);
        }
    }
}
