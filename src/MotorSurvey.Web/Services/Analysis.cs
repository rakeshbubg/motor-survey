using System.Threading.Channels;
using MotorSurvey.Core;

namespace MotorSurvey.Web.Services;

/// <summary>In-process job queue. Swap for RabbitMQ / Azure Service Bus when AI workers run on their own servers.</summary>
public sealed class AnalysisQueue
{
    private readonly Channel<string> _ch = Channel.CreateUnbounded<string>();
    public ValueTask EnqueueAsync(string claimNo) => _ch.Writer.WriteAsync(claimNo);
    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct) => _ch.Reader.ReadAllAsync(ct);
}

public sealed class AnalysisWorker(AnalysisQueue queue, IServiceProvider sp, ILogger<AnalysisWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await foreach (var claimNo in queue.ReadAllAsync(stop))
        {
            try
            {
                using var scope = sp.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ClaimService>().RunAnalysisAsync(claimNo, stop);
            }
            catch (Exception ex) when (!stop.IsCancellationRequested) { log.LogError(ex, "Analysis failed for {Claim}", claimNo); }
        }
    }
}
