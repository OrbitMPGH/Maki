using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Mangarr.Data;

/// <summary>Used only by `dotnet ef` tooling; the runtime connection is configured in Mangarr.Api.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MangarrDbContext>
{
    public MangarrDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MangarrDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new MangarrDbContext(options);
    }
}
