using System.Text.Json;
using Maki.Api.Services;
using Xunit;

namespace Maki.Api.Tests;

public class MemoryDiagnosticsTests
{
    /// <summary>
    /// The gen0 budget is what made two hosts on one build incomparable, and it is invisible from
    /// outside the process, so the endpoint reports what the GC resolved.
    /// </summary>
    [Fact]
    public void The_gc_settings_report_what_the_runtime_resolved()
    {
        var settings = JsonSerializer.SerializeToElement(MemoryDiagnostics.GcSettings());

        var budget = settings.GetProperty("gen0MaxBudgetBytes");
        Assert.Equal(JsonValueKind.Number, budget.ValueKind);
        Assert.Equal(
            Convert.ToInt64(GC.GetConfigurationVariables()["GCGen0MaxBudget"]),
            budget.GetInt64());
    }
}
