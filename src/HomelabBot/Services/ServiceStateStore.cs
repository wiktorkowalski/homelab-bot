using HomelabBot.Data;
using HomelabBot.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HomelabBot.Services;

public class ServiceStateStore
{
    private readonly IDbContextFactory<HomelabDbContext> _dbFactory;
    private readonly ILogger<ServiceStateStore> _logger;

    public ServiceStateStore(IDbContextFactory<HomelabDbContext> dbFactory, ILogger<ServiceStateStore> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public virtual async Task<string?> GetAsync(string serviceName, string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.ServiceStates
            .FirstOrDefaultAsync(s => s.ServiceName == serviceName && s.Key == key);

        _logger.LogInformation("State {ServiceName}/{Key}: {Result}", serviceName, key, entry is null ? "not found" : "found");
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

        try
        {
            await db.SaveChangesAsync();
            _logger.LogInformation("{Operation} state for {ServiceName}/{Key}", entry != null ? "Updated" : "Created", serviceName, key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save state for {ServiceName}/{Key}", serviceName, key);
            throw;
        }
    }
}
