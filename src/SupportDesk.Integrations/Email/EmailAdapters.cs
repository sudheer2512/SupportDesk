using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDesk.Integrations;

/// <summary>E-mail channel providers: IMAP/SMTP, Microsoft 365 (Graph), Gmail API, file-drop (development).</summary>
public static class EmailAdapters
{
    public static IServiceCollection AddEmailAdapters(this IServiceCollection s, IConfiguration configuration)
    {
        return s;
    }
}
