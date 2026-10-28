using System.Reflection;
using Maki.Api.Configuration;
using Maki.Api.Jobs;
using Maki.Api.Services;

namespace Maki.Api.Tests;

public sealed class ConfigDirCollectionUsageTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const byte Call = 0x28;
    private const byte Callvirt = 0x6F;
    private const byte Ldstr = 0x72;
    private const byte Newobj = 0x73;

    private static readonly ConstructorInfo FromEnvironment = typeof(AppPaths).GetConstructor(Type.EmptyTypes)!;
    private static readonly MethodInfo Scanner = typeof(UpgradeWorld).GetMethod(nameof(UpgradeWorld.Scanner))!;
    private static readonly Type[] OneRunPerProcess = [typeof(UpgradeScanService), typeof(UpgradeVolumeSearchJob), typeof(HealthMonitor)];

    private static IEnumerable<Type> TestTypes() =>
        typeof(ConfigDirCollectionUsageTests).Assembly.GetTypes()
            .Where(t => !t.IsNested && t != typeof(ConfigDirCollection) && t != typeof(ConfigDirCollectionUsageTests));

    private static bool InCollection(Type type, string name) => type.GetCustomAttributesData().Any(a =>
        a.AttributeType == typeof(CollectionAttribute) && (string?)a.ConstructorArguments[0].Value == name);

    /// <summary>
    /// Without a directory, AppPaths falls back to MAKI_CONFIG_DIR and then the real
    /// %APPDATA%\Maki, which a test must never write to. Pass the test's own directory instead.
    /// </summary>
    [Fact]
    public void No_test_builds_AppPaths_from_the_environment()
    {
        var offenders = TestTypes()
            .Where(t => Loads(t, [Newobj], operand => operand is ConstructorInfo c && c == FromEnvironment))
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_class_that_touches_MAKI_CONFIG_DIR_joins_the_config_dir_collection()
    {
        var offenders = TestTypes()
            .Where(t => Loads(t, [Ldstr], operand => operand is "MAKI_CONFIG_DIR"))
            .Where(t => !InCollection(t, ConfigDirCollection.Name))
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_class_that_runs_a_one_run_per_process_service_joins_its_collection()
    {
        var offenders = TestTypes()
            .Where(t => t != typeof(UpgradeWorld))
            .Where(t => Loads(t, [Call, Callvirt, Newobj], operand => operand switch
            {
                MethodInfo m => m == Scanner,
                ConstructorInfo c => OneRunPerProcess.Contains(c.DeclaringType),
                _ => false,
            }))
            .Where(t => !InCollection(t, OneRunPerProcessCollection.Name))
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>Whether any method of <paramref name="type"/>, lambdas and state machines included, has one of <paramref name="opcodes"/> with an operand that matches.</summary>
    private static bool Loads(Type type, byte[] opcodes, Func<object, bool> matches)
    {
        var methods = type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All));
        foreach (var method in methods)
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il is null)
            {
                continue;
            }

            // A byte scan rather than a decoder: an operand byte can look like the opcode, so every
            // candidate token is resolved and only a real match counts.
            for (var i = 0; i + 4 < il.Length; i++)
            {
                if (Array.IndexOf(opcodes, il[i]) < 0)
                {
                    continue;
                }

                var token = BitConverter.ToInt32(il, i + 1);
                object? operand;
                try
                {
                    operand = il[i] == Ldstr ? type.Module.ResolveString(token) : type.Module.ResolveMethod(token);
                }
                catch (Exception e) when (e is ArgumentException or BadImageFormatException or MissingMemberException or TypeLoadException)
                {
                    continue;
                }

                if (operand is not null && matches(operand))
                {
                    return true;
                }
            }
        }

        return type.GetNestedTypes(All).Any(n => Loads(n, opcodes, matches));
    }
}
