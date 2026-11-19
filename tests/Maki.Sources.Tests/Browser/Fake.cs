using System.Reflection;

namespace Maki.Sources.Tests.Browser;

/// <summary>
/// A stand-in for a large PuppeteerSharp interface (IBrowser, IResponse) without a mocking library:
/// calls go to <see cref="Handle"/> by member name, and anything it does not answer returns the
/// type's default, or a completed task for an async member.
/// </summary>
public class Fake<T> : DispatchProxy where T : class
{
    public Func<string, object?[]?, object?>? Handle { get; set; }

    public List<string> Calls { get; } = [];

    public static (T Instance, Fake<T> Proxy) Create(Func<string, object?[]?, object?>? handle = null)
    {
        var instance = DispatchProxy.Create<T, Fake<T>>();
        var proxy = (Fake<T>)(object)instance;
        proxy.Handle = handle;
        return (instance, proxy);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        lock (Calls)
        {
            Calls.Add(name);
        }

        if (Handle?.Invoke(name, args) is { } answer)
        {
            return answer;
        }

        var type = targetMethod.ReturnType;
        if (type == typeof(void))
        {
            return null;
        }

        if (type == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (type == typeof(ValueTask))
        {
            return ValueTask.CompletedTask;
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
