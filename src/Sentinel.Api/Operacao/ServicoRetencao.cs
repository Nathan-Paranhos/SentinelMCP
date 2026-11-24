using Sentinel.Core.Contratos;

namespace Sentinel.Api.Operacao;

// Serviço em segundo plano para expurgo de registros antigos
public sealed class ServicoRetencao : BackgroundService
{
    private readonly IManutencaoRetencao _manutencao;
    private readonly IConfiguration _config;
    private readonly ILogger<ServicoRetencao> _log;

    public ServicoRetencao(IManutencaoRetencao manutencao, IConfiguration config, ILogger<ServicoRetencao> log)
    {
        _manutencao = manutencao;
        _config = config;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dias = Math.Max(1, _config.GetValue("Sentinel:Retention:ReceiptRetentionDays", 30));
        var intervalo = TimeSpan.FromMinutes(Math.Max(1, _config.GetValue("Sentinel:Retention:IntervalMinutes", 360)));

        using var timer = new PeriodicTimer(intervalo);
        do
        {
            try
            {
                var removidos = await _manutencao.ExpurgarRecibosAsync(DateTimeOffset.UtcNow.AddDays(-dias), stoppingToken);
                if (removidos > 0)
                    _log.LogInformation("Retenção: {Removidos} recibo(s)/comprovante(s) com mais de {Dias} dias removidos.", removidos, dias);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falha no job de retenção; nova tentativa no próximo ciclo.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
