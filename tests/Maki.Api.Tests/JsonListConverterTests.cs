using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public class JsonListConverterTests
{
    [Fact]
    public async Task Unreadable_conditions_read_as_empty_and_log_a_warning()
    {
        var logger = new CapturingLogger();
        var previous = DataDiagnostics.Logger;
        DataDiagnostics.Logger = logger;
        try
        {
            using var fixture = new TestDb();
            using (var db = fixture.NewContext())
            {
                const string stale = "[{\"Type\":\"NoSuchType\",\"Value\":\"x\",\"Required\":true,\"Negate\":false}]";
                db.Database.ExecuteSql($"INSERT INTO QualityFormats (Name, Conditions, Version) VALUES ('Old', {stale}, 1)");
            }

            using var read = fixture.NewContext();
            var format = await read.QualityFormats.SingleAsync(f => f.Name == "Old");

            Assert.Empty(format.Conditions);
            Assert.Contains(logger.Warnings, w => w.Contains(nameof(FormatCondition)));
        }
        finally
        {
            DataDiagnostics.Logger = previous;
        }
    }

    [Fact]
    public async Task Unreadable_genres_read_as_empty_and_log_a_warning()
    {
        var logger = new CapturingLogger();
        var previous = DataDiagnostics.Logger;
        DataDiagnostics.Logger = logger;
        try
        {
            using var fixture = new TestDb();
            var id = fixture.SeedSeries();
            using (var db = fixture.NewContext())
            {
                db.Database.ExecuteSql($"UPDATE Series SET Genres = 'not json' WHERE Id = {id}");
            }

            using var read = fixture.NewContext();
            var loaded = await read.Series.SingleAsync(s => s.Id == id);

            Assert.Empty(loaded.Genres);
            Assert.Contains(logger.Warnings, w => w.Contains("string list"));
        }
        finally
        {
            DataDiagnostics.Logger = previous;
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                lock (Warnings) Warnings.Add(formatter(state, exception));
            }
        }
    }
}
