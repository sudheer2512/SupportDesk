using Microsoft.Extensions.Logging;
using SupportDesk.Application.Integrations.Erp;
using SupportDesk.Domain;
using SupportDesk.Persistence;

namespace SupportDesk.Integrations.Erp;

/// <summary>
/// Builds the connector for a configured ERP integration: the HTTP connector (mode "http") or the deterministic
/// Wings ERP simulator (mode "simulator", development and demo).
/// </summary>
public sealed class ErpConnectorFactory(
    IHttpClientFactory httpFactory,
    ErpTokenCache tokens,
    IErpCallLog callLog,
    DeskDbContext db,
    IClock clock,
    ILoggerFactory loggers) : IErpConnectorFactory
{
    public IErpConnector Create(ErpConnection connection)
    {
        if (string.Equals(connection.Settings.Mode, ErpModes.Http, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(connection.BaseUrl))
                throw new DomainException("integration_unavailable", $"{connection.System} has no API base URL configured.");
            return new WingsErpHttpConnector(httpFactory.CreateClient(WingsErpHttpConnector.ClientName), connection, tokens, callLog,
                loggers.CreateLogger<WingsErpHttpConnector>());
        }
        return new WingsErpSimulator(connection, new SimulatorUniverse(db), clock);
    }
}
