using HomelabBot.Configuration;
using HomelabBot.Data.Entities;
using HomelabBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomelabBot.Tests;

public class WarRoomServiceTests : IClassFixture<DatabaseFixture>, IDisposable
{
    private readonly DatabaseFixture _fixture;

    public WarRoomServiceTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public void Dispose()
    {
        using var db = _fixture.DbContextFactory.CreateDbContext();
        db.WarRooms.RemoveRange(db.WarRooms);
        db.SaveChanges();
    }

    [Fact]
    public void ShouldOpenWarRoom_Enabled_CriticalSeverity_ReturnsTrue()
    {
        var service = CreateService(new WarRoomConfiguration
        {
            Enabled = true,
            ChannelId = 123,
            TriggerSeverities = ["critical"]
        });

        Assert.True(service.ShouldOpenWarRoom("critical"));
    }

    [Fact]
    public void ShouldOpenWarRoom_Disabled_ReturnsFalse()
    {
        var service = CreateService(new WarRoomConfiguration { Enabled = false });

        Assert.False(service.ShouldOpenWarRoom("critical"));
    }

    [Fact]
    public void ShouldOpenWarRoom_WrongSeverity_ReturnsFalse()
    {
        var service = CreateService(new WarRoomConfiguration
        {
            Enabled = true,
            ChannelId = 123,
            TriggerSeverities = ["critical"]
        });

        Assert.False(service.ShouldOpenWarRoom("warning"));
    }

    [Fact]
    public void ShouldOpenWarRoom_NoChannelId_ReturnsFalse()
    {
        var service = CreateService(new WarRoomConfiguration
        {
            Enabled = true,
            ChannelId = 0,
            TriggerSeverities = ["critical"]
        });

        Assert.False(service.ShouldOpenWarRoom("critical"));
    }

    [Fact]
    public async Task WarRoom_PersistsToDb()
    {
        await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

        db.WarRooms.Add(new WarRoom
        {
            DiscordThreadId = 12345,
            StatusMessageId = 67890,
            Trigger = "nginx down",
            Severity = "critical"
        });
        await db.SaveChangesAsync();

        var saved = await db.WarRooms.FirstAsync();
        Assert.Equal("nginx down", saved.Trigger);
        Assert.Equal(WarRoomStatus.Active, saved.Status);
        Assert.Null(saved.ResolvedAt);
    }

    [Fact]
    public async Task GetActiveWarRoom_ReturnsActive()
    {
        await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
        {
            db.WarRooms.Add(new WarRoom
            {
                DiscordThreadId = 111,
                StatusMessageId = 222,
                Trigger = "postgres high cpu",
                Severity = "critical"
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService();
        var active = await service.GetActiveWarRoomAsync();

        Assert.NotNull(active);
        Assert.Equal("postgres high cpu", active.Trigger);
    }

    [Fact]
    public async Task GetActiveWarRoom_WithContainer_FiltersByTrigger()
    {
        await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
        {
            db.WarRooms.Add(new WarRoom
            {
                DiscordThreadId = 333,
                StatusMessageId = 444,
                Trigger = "nginx container down",
                Severity = "critical"
            });
            db.WarRooms.Add(new WarRoom
            {
                DiscordThreadId = 555,
                StatusMessageId = 666,
                Trigger = "postgres high memory",
                Severity = "critical"
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService();

        var nginx = await service.GetActiveWarRoomAsync("nginx");
        Assert.NotNull(nginx);
        Assert.Contains("nginx", nginx.Trigger);

        var postgres = await service.GetActiveWarRoomAsync("postgres");
        Assert.NotNull(postgres);
        Assert.Contains("postgres", postgres.Trigger);

        var redis = await service.GetActiveWarRoomAsync("redis");
        Assert.Null(redis);
    }

    [Fact]
    public void WarRoomEntity_DefaultValues()
    {
        var warRoom = new WarRoom
        {
            Trigger = "test",
            Severity = "critical"
        };

        Assert.Equal(WarRoomStatus.Active, warRoom.Status);
        Assert.Equal("[]", warRoom.TimelineJson);
        Assert.Null(warRoom.ResolvedAt);
        Assert.Null(warRoom.Resolution);
        Assert.Null(warRoom.PostMortemSummary);
        Assert.Null(warRoom.Mttr);
    }

    private WarRoomService CreateService(WarRoomConfiguration? config = null)
    {
        config ??= new WarRoomConfiguration();
        return new WarRoomService(
            _fixture.DbContextFactory,
            null!, // DiscordBotService not needed for DB/config tests
            Options.Create(config),
            NullLogger<WarRoomService>.Instance);
    }
}
