using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

public class DbErrorsTests
{
    [Fact]
    public void Unique_violation_is_recognised_wrapped_and_bare_but_not_other_constraints()
    {
        using var fixture = new TestDb();
        using var db = fixture.NewContext();
        db.Database.ExecuteSqlRaw("CREATE TABLE T (Id INTEGER PRIMARY KEY, V TEXT NOT NULL UNIQUE)");
        db.Database.ExecuteSqlRaw("INSERT INTO T (Id, V) VALUES (1, 'a')");

        var unique = Assert.ThrowsAny<Exception>(() => db.Database.ExecuteSqlRaw("INSERT INTO T (V) VALUES ('a')"));
        var notNull = Assert.ThrowsAny<Exception>(() => db.Database.ExecuteSqlRaw("INSERT INTO T (V) VALUES (NULL)"));

        Assert.True(DbErrors.IsUniqueViolation(unique));
        Assert.True(DbErrors.IsUniqueViolation(new DbUpdateException("x", unique)));
        Assert.False(DbErrors.IsUniqueViolation(notNull));
        Assert.False(DbErrors.IsUniqueViolation(new DbUpdateException("x", notNull)));
        Assert.False(DbErrors.IsUniqueViolation(new InvalidOperationException()));
    }
}
