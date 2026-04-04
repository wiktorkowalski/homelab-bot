using HomelabBot.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomelabBot.Tests;

public class ServiceStateStoreTests : IClassFixture<DatabaseFixture>
{
    private readonly ServiceStateStore _store;

    public ServiceStateStoreTests(DatabaseFixture fixture)
    {
        _store = new ServiceStateStore(fixture.DbContextFactory, NullLogger<ServiceStateStore>.Instance);
    }

    [Fact]
    public async Task SetThenGet_ReturnsValue()
    {
        var service = $"svc-{Guid.NewGuid()}";

        await _store.SetAsync(service, "key1", "hello");
        var result = await _store.GetAsync(service, "key1");

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task UpdateExistingKey_ReturnsLatestValue()
    {
        var service = $"svc-{Guid.NewGuid()}";

        await _store.SetAsync(service, "key1", "first");
        await _store.SetAsync(service, "key1", "second");
        var result = await _store.GetAsync(service, "key1");

        Assert.Equal("second", result);
    }

    [Fact]
    public async Task GetNonexistent_ReturnsNull()
    {
        var result = await _store.GetAsync("no-such-service", "no-such-key");

        Assert.Null(result);
    }

    [Fact]
    public async Task DifferentServiceNames_SameKey_DontCollide()
    {
        var svcA = $"svc-a-{Guid.NewGuid()}";
        var svcB = $"svc-b-{Guid.NewGuid()}";

        await _store.SetAsync(svcA, "shared-key", "value-a");
        await _store.SetAsync(svcB, "shared-key", "value-b");

        Assert.Equal("value-a", await _store.GetAsync(svcA, "shared-key"));
        Assert.Equal("value-b", await _store.GetAsync(svcB, "shared-key"));
    }
}
