using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

public class PendingModelChangesTests
{
    [Fact]
    public void Every_model_change_has_a_migration()
    {
        using var db = new MakiDbContext(new DbContextOptionsBuilder<MakiDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options);

        Assert.False(db.Database.HasPendingModelChanges(),
            "The EF model has changes no migration covers. Add one with: dotnet ef migrations add <Name> " +
            "--project src/Maki.Data --startup-project src/Maki.Api");
    }
}
