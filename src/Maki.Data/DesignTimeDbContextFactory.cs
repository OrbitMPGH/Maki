using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Maki.Data;

/// <summary>Used only by `dotnet ef` tooling; the runtime connection is configured in Maki.Api.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MakiDbContext>
{
    public MakiDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MakiDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new MakiDbContext(options);
    }
}
