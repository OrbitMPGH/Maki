namespace Maki.Sources.Common;

/// <summary>
/// Caps the Playwright Node driver's V8 heap. Every image TopManhua captures crosses the driver as
/// base64 JSON, and an uncapped driver grew from 120 to 260 MB over a burst and kept it until it
/// exited. With both flags it peaked at about 145 MB and used less CPU. The two only work together:
/// the old-space cap alone made the driver collect constantly (4 to 5 times the CPU), and a 48 MB
/// cap ran out of heap on the first chapter.
///
/// <para>
/// Playwright copies the process environment into the driver's start info and offers no other way
/// in, so the variable is set only for the length of <c>Playwright.CreateAsync</c> and removed
/// again. An admin's own <c>NODE_OPTIONS</c> wins and is never touched.
/// </para>
/// </summary>
public static class DriverHeapCap
{
    public const string Variable = "NODE_OPTIONS";

    public const string Value = "--max-semi-space-size=1 --max-old-space-size=128";

    /// <summary>
    /// Sets the cap until the returned scope is disposed. Null, and nothing changed, when
    /// <c>NODE_OPTIONS</c> is already set.
    /// </summary>
    public static IDisposable? Apply()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
        {
            return null;
        }

        Environment.SetEnvironmentVariable(Variable, Value);
        return new Scope();
    }

    /// <summary>
    /// True when <paramref name="value"/> is the cap this class sets, which another Node process
    /// started in that short window (the browser install) should not inherit.
    /// </summary>
    public static bool IsOurs(string? value) => value == Value;

    private sealed class Scope : IDisposable
    {
        public void Dispose()
        {
            if (IsOurs(Environment.GetEnvironmentVariable(Variable)))
            {
                Environment.SetEnvironmentVariable(Variable, null);
            }
        }
    }
}
