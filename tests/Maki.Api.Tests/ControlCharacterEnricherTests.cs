using Maki.Api.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Maki.Api.Tests;

public class ControlCharacterEnricherTests
{
    [Fact]
    public void A_line_break_in_a_value_cannot_start_a_new_log_line()
    {
        var line = Render("Search for {Query} by {User}", "x\r\n2026-01-01 [ERR] Auth: forged", "reader");

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Equal(@"Search for x\r\n2026-01-01 [ERR] Auth: forged by reader", line);
    }

    [Fact]
    public void Other_control_characters_are_escaped_and_tabs_kept()
    {
        Assert.Equal("a\\u001b[31mb\tc", ControlCharacterEnricher.Escape("a\u001b[31mb\tc"));
    }

    [Fact]
    public void Plain_values_are_left_alone()
    {
        Assert.Equal("Search for Berserk by reader", Render("Search for {Query} by {User}", "Berserk", "reader"));
    }

    private static string Render(string template, params object[] values)
    {
        var sink = new CapturingSink();
        using (var logger = new LoggerConfiguration().Enrich.With<ControlCharacterEnricher>().WriteTo.Sink(sink).CreateLogger())
        {
            logger.Information(template, values);
        }

        var output = new StringWriter();
        new MessageTemplateTextFormatter("{Message:lj}").Format(Assert.Single(sink.Events), output);
        return output.ToString();
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
