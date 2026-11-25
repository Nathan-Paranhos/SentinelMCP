# Contribuindo para o Sentinel-MCP

Obrigado pelo interesse em contribuir com o **Sentinel-MCP**! Este projeto é open source sob a [licença MIT](LICENSE) e foi projetado para rodar tanto localmente na máquina de cada pessoa quanto em produção dentro de uma organização.

---

## 1. Pré-requisitos

- **.NET 9.0 SDK** (`dotnet --version`; compatível também com roll-forward em .NET 10)
- **Node.js** (opcional, apenas para checagem de sintaxe `node --check` em scripts frontend)
- **Docker** (opcional localmente; necessário para os 24 testes de integração com PostgreSQL real via Testcontainers e para `docker compose`)

---

## 2. Fluxo de Desenvolvimento Local

```bash
git clone https://github.com/Nathan-Paranhos/SentinelMCP.git
cd SentinelMCP
dotnet restore Sentinel.sln
dotnet build Sentinel.sln -warnaserror
```

### Rodando os testes
```bash
dotnet test tests/Sentinel.UnitTests
dotnet test tests/Sentinel.ContractTests
dotnet test tests/Sentinel.IntegrationTests
```
> Sem Docker, os 24 testes contra PostgreSQL aparecem como *skipped*. No CI (`.github/workflows/ci.yml`), `SENTINEL_REQUIRE_DOCKER=1` garante que toda a suíte PostgreSQL seja executada.

### Rodando localmente com interface
```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/Sentinel.Api
```
Acesse `http://localhost:5000/review`. Os dados são salvos em `src/Sentinel.Api/data/sentinel-local.json` (ignorado pelo Git).

---

## 3. Regras de Arquitetura e Qualidade

Consulte o guia completo em [AGENTS.md](AGENTS.md). As invariantes principais são:

1. **Regra de negócio e autorização ficam em [`ServicoDeMemoria.cs`](src/Sentinel.Core/CasosDeUso/ServicoDeMemoria.cs)**: não duplique validações nem regras de papel nos endpoints MCP ou web.
2. **Projeções vêm sempre dos redutores**: nunca escreva em `decision_current`, `incident_current` ou `decision_pending_proposals` fora de `RedutorDeDecisao` / `RedutorDeIncidente`.
3. **Histórico é estritamente `append-only`**: `memory_events` e `memory_payloads` nunca são editados. Mudanças de schema no PostgreSQL devem entrar como uma **nova migração numerada** em `db/migrations/` (ex.: `007_*.sql`), nunca alterando migrações anteriores.
4. **Zero segredos versionados**: nunca coloque senhas, strings de conexão reais ou chaves de API em `appsettings*.json`, `compose.yaml` ou fixtures.
5. **Frontend e Design System**:
   - Mantenha CSP estrita (`script-src 'self'`, **nenhum** script inline em HTML e **nenhuma** CDN externa).
   - Use os tokens CSS semânticos de [`tokens.css`](src/Sentinel.Api/wwwroot/css/tokens.css) descritos em [docs/design/DESIGN_SYSTEM.md](docs/design/DESIGN_SYSTEM.md).
   - Teste o JS modificado com `node --check` nos arquivos alterados em `src/Sentinel.Api/wwwroot/js/`.

---

## 4. Checklist Antes de Abrir um Pull Request

- [ ] `dotnet build Sentinel.sln -warnaserror` compila sem avisos ou erros.
- [ ] `dotnet test Sentinel.sln` passa integralmente (`UnitTests`, `ContractTests`, `IntegrationTests`).
- [ ] Mudanças de schema incluem `GRANT`s mínimos para `sentinel_app` e respeitam `FORCE ROW LEVEL SECURITY`.
- [ ] Documentação atualizada ([`README.md`](README.md), [`docs/`](docs/README.md) ou [`AGENTS.md`](AGENTS.md)) se houver novo endpoint, variável de ambiente ou alteração de fluxo.
- [ ] Sem arquivos `data/`, `.env` ou tokens reais incluídos no commit.
