using Microsoft.EntityFrameworkCore;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Messages;

/// <summary>
/// Archives inactive message conversations once their most recent message is older than the retention window.
/// </summary>
public sealed class MessageArchiveService
{
    private readonly RequestPortalDbContext dbContext;

    public MessageArchiveService(RequestPortalDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task<int> ArchiveInactiveThreadsAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        Guid[] threadIds = await dbContext.MessageThreads
            .Where(thread =>
                thread.ClosedUtc == null &&
                dbContext.PortalMessages.Any(message => message.ThreadId == thread.Id) &&
                dbContext.PortalMessages
                    .Where(message => message.ThreadId == thread.Id)
                    .Max(message => message.SentUtc) < cutoffUtc)
            .Select(thread => thread.Id)
            .ToArrayAsync(cancellationToken);

        if (threadIds.Length == 0)
        {
            return 0;
        }

        DateTimeOffset archivedUtc = DateTimeOffset.UtcNow;
        List<RequestPortal.Domain.Messages.MessageThread> threads = await dbContext.MessageThreads
            .Where(thread => threadIds.Contains(thread.Id))
            .ToListAsync(cancellationToken);

        foreach (RequestPortal.Domain.Messages.MessageThread thread in threads)
        {
            thread.Close(archivedUtc);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return threads.Count;
    }
}
