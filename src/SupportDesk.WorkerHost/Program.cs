using SupportDesk.Application;
using SupportDesk.Domain;
using SupportDesk.Integrations;
using SupportDesk.Persistence.Infrastructure;
using SupportDesk.Workers;

// Out-of-process worker host: durable jobs and recurring tasks (SLA monitor, mailbox polling, notifications, analytics,
// retention…). Runs as a Windows Service or systemd unit. API nodes then set Workers:Jobs=false and Workers:Recurring=false;
// the outbox dispatcher normally stays on API nodes so realtime pushes originate where SignalR hubs run.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "SupportDesk Workers");
builder.Services.AddSystemd();
if (!builder.Environment.IsDevelopment()) builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);

builder.Services.AddSingleton<MutableClock>();
builder.Services.AddSingleton<IClock>(sp => sp.GetRequiredService<MutableClock>());
builder.Services.AddDeskPersistence(builder.Configuration);
builder.Services.AddDeskApplication(builder.Configuration);
builder.Services.AddDeskIntegrations(builder.Configuration);
builder.Services.AddDeskWorkers(builder.Configuration);

var host = builder.Build();
using (var scope = host.Services.CreateScope())
{
    // The API normally applies migrations; the worker host never seeds demo data.
    if (builder.Configuration.GetValue("Database:MigrateOnStart", false))
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
}
await host.RunAsync();
