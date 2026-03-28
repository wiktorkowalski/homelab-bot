using HomelabBot.Data;
using HomelabBot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HomelabBot.Services;

public class ServiceStateStore
{
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;

    public ServiceStateStore(IDbContextFactory<HomelabDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public virtual async Task<string?> GetAsync(string serviceName, string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.ServiceStates
            .FirstOrDefaultAsync(s => s.ServiceName == serviceName && s.Key == key);
        return entry?.Value;
    }

    public virtual async Task SetAsync(string serviceName, string key, string value)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.ServiceStates
            .FirstOrDefaultAsync(s => s.ServiceName == serviceName && s.Key == key);

        if (entry != null)
        {
            entry.Value = value;
            entry.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            db.ServiceStates.Add(new ServiceState
            {
                ServiceName = serviceName,
                Key = key,
                Value = value
            });
        }

        await db.SaveChangesAsync();
    }
}
