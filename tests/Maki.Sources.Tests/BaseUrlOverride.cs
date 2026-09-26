namespace Maki.Sources.Tests;

/// <summary>
/// MAKI_SOURCE_*_BASEURL is process-global and sources read it on every call, so a class that sets
/// one must not run alongside the classes that assert default-host URLs.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BaseUrlOverrideCollection
{
    public const string Name = "base-url-override";
}

internal sealed class BaseUrlOverride : IDisposable
{
    private readonly string _variable;
    private readonly string? _prior;

    public BaseUrlOverride(string variable, string value)
    {
        _variable = variable;
        _prior = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_variable, _prior);
}
