using System.Reflection;

namespace Maki.Api.Tests;

public sealed class ConfigDirCollectionUsageTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void Every_class_that_builds_an_UpgradeWorld_joins_the_config_dir_collection()
    {
        var ctorNewobj = new[] { (byte)0x73 }
            .Concat(BitConverter.GetBytes(typeof(UpgradeWorld).GetConstructor(Type.EmptyTypes)!.MetadataToken))
            .ToArray();

        var offenders = typeof(UpgradeWorld).Assembly.GetTypes()
            .Where(t => t != typeof(UpgradeWorld) && !t.IsNested)
            .Where(t => UsesUpgradeWorld(t, ctorNewobj))
            .Where(t => !t.GetCustomAttributesData().Any(a => a.AttributeType == typeof(CollectionAttribute) && (string?)a.ConstructorArguments[0].Value == ConfigDirCollection.Name))
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    private static bool UsesUpgradeWorld(Type type, byte[] newobj)
    {
        if (type.GetFields(All).Any(f => f.FieldType == typeof(UpgradeWorld))) return true;

        var methods = type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All));
        if (methods.Any(m => Contains(m.GetMethodBody()?.GetILAsByteArray(), newobj))) return true;

        return type.GetNestedTypes(All).Any(n => UsesUpgradeWorld(n, newobj));
    }

    private static bool Contains(byte[]? il, byte[] pattern)
    {
        if (il is null) return false;
        return il.AsSpan().IndexOf(pattern) >= 0;
    }
}
