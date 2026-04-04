using HomelabBot.Data;
using Microsoft.EntityFrameworkCore;

namespace HomelabBot.Tests;

public class MigrationDriftTests
{
    [Fact]
    public void Model_has_no_pending_changes()
    {
        var options = new DbContextOptionsBuilder<HomelabDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        using var context = new HomelabDbContext(options);

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "Model has pending changes. Run: dotnet ef migrations add <Name> --project src/HomelabBot");
    }
}
