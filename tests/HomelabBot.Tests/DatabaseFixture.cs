using HomelabBot.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HomelabBot.Tests;

public class DatabaseFixture : IDisposable
{
    private readonly SqliteConnection _connection;

    public IDbContextFactory<HomelabDbContext> DbContextFactory { get; }

    public DatabaseFixture()
    {
        // Use in-memory SQLite with a shared connection
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<HomelabDbContext>()
            .UseSqlite(_connection)
            .Options;

        // Create the schema
        using (var context = new HomelabDbContext(options))
        {
            context.Database.EnsureCreated();
        }

        DbContextFactory = new TestDbContextFactory(options);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<HomelabDbContext>
    {
        private readonly DbContextOptions<HomelabDbContext> _options;

        public TestDbContextFactory(DbContextOptions<HomelabDbContext> options)
        {
            _options = options;
        }

        public HomelabDbContext CreateDbContext()
        {
            return new HomelabDbContext(_options);
        }
    }
}
