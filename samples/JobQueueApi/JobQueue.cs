using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace JobQueueApi;

/// <summary>
/// The in-memory queue (a bounded channel of jobs) plus the lookup table the API
/// reads. Everything is lost on restart: this sample keeps no durable state.
/// </summary>
public sealed class JobQueue
{
    private readonly Channel<Job> _channel;
    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();
    private readonly int _maxRetained;

    public JobQueue(IOptions<JobQueueOptions> options)
    {
        _channel = Channel.CreateBounded<Job>(new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait // TryWrite returns false when full
        });
        _maxRetained = options.Value.MaxRetainedJobs;
    }

    /// <summary>Queues the job, or returns false when the queue is full.</summary>
    public bool TryEnqueue(Job job)
    {
        _jobs[job.Id] = job;
        if (_channel.Writer.TryWrite(job))
        {
            PruneFinished();
            return true;
        }
        _jobs.TryRemove(job.Id, out _);
        return false;
    }

    public Job? Find(Guid id) => _jobs.GetValueOrDefault(id);

    public IEnumerable<Job> All() => _jobs.Values.OrderByDescending(j => j.CreatedAt);

    public IAsyncEnumerable<Job> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    private void PruneFinished()
    {
        var finished = _jobs.Values.Where(j => j.IsFinished).OrderBy(j => j.CreatedAt).ToList();
        foreach (var job in finished.Take(Math.Max(0, finished.Count - _maxRetained)))
        {
            _jobs.TryRemove(job.Id, out _);
        }
    }
}
