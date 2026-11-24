using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Sentinel.Api.Operacao;

public static class SentinelDiagnostics
{
    public const string ServiceName = "Sentinel-MCP";
    public const string ServiceVersion = "0.2.0";

    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    public static readonly Counter<long> McpToolExecutions = Meter.CreateCounter<long>(
        "sentinel_mcp_tool_calls_total",
        unit: "calls",
        description: "Total de chamadas de tools MCP executadas");

    public static readonly Counter<long> McpToolErrors = Meter.CreateCounter<long>(
        "sentinel_mcp_tool_errors_total",
        unit: "errors",
        description: "Total de erros retornados em chamadas de tools MCP");
}
