using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Persistence;

namespace SupportDesk.Workers;

public sealed class WorkerOptions
{
    /// <summary>Master switch (the API can run without workers when a separate WorkerHost runs them).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Dispatch outbox events. Should run where SignalR hubs are hosted so realtime pushes reach clients.</summary>
    public bool Outbox { get; set; } = true;
    public bool Jobs { get; set; } = true;
    public bool Recurring { get; set; } = true;
    public int JobConcurrency { get; set; } = 4;
    public string InstanceName { get; set; } = $"{Environment.MachineName}-{Environment.ProcessId}";
}

/// <summary>In-process wake-up signal so the dispatcher reacts to new outbox rows immediately instead of waiting for the next poll.</summary>
public sealed class OutboxSignal : IOutboxSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Notify()
    {
        if (_semaphore.CurrentCount == 0)
        {
            try { _semaphore.Release(); } catch (SemaphoreFullException) { }
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _semaphore.WaitAsync(timeout, ct);
}

public static class WorkerServices
{
    public static IServiceCollection AddDeskWorkers(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Workers").Get<WorkerOptions>() ?? new WorkerOptions();
        services.AddSingleton(options);
        services.AddSingleton<OutboxSignal>();
        services.AddSingleton<IOutboxSignal>(sp => sp.GetRequiredService<OutboxSignal>());
        services.AddSingleton<OutboxProcessor>();
        services.AddSingleton<JobProcessor>();
        services.AddSingleton<RecurringTaskProcessor>();
        if (!options.Enabled) return services;
        if (options.Outbox) services.AddHostedService<OutboxDispatcher>();
        if (options.Jobs) services.AddHostedService<JobRunner>();
        if (options.Recurring) services.AddHostedService<RecurringTaskRunner>();
        return services;
    }
}
