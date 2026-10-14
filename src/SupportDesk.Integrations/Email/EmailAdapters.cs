using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Communication;
using SupportDesk.Integrations.Email;

namespace SupportDesk.Integrations;

/// <summary>E-mail channel providers: IMAP/SMTP, Microsoft 365 (Graph), Gmail API, file-drop (development).</summary>
public static class EmailAdapters
{
    public static IServiceCollection AddEmailAdapters(this IServiceCollection s, IConfiguration configuration)
    {
        s.AddSingleton<IMimeCodec, MimeCodec>();
        s.AddTransient<FileDropEmailProvider>();
        s.AddTransient<IEmailProvider>(sp => sp.GetRequiredService<FileDropEmailProvider>());
        s.AddTransient<IEmailProvider, ImapSmtpEmailProvider>();
        s.AddTransient<IEmailProvider, SmtpEmailProvider>();

        s.AddHttpClient<GraphEmailProvider>(c => c.Timeout = TimeSpan.FromSeconds(100)).AddStandardResilienceHandler();
        s.AddTransient<IEmailProvider>(sp => sp.GetRequiredService<GraphEmailProvider>());
        s.AddHttpClient<GmailEmailProvider>(c => c.Timeout = TimeSpan.FromSeconds(100)).AddStandardResilienceHandler();
        s.AddTransient<IEmailProvider>(sp => sp.GetRequiredService<GmailEmailProvider>());
        return s;
    }
}
