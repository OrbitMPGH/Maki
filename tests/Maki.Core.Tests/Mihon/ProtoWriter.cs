using System.Text;

namespace Maki.Core.Tests.Mihon;

/// <summary>Builds protobuf messages the way kotlinx.serialization writes them: plain varints, no zigzag.</summary>
internal sealed class ProtoWriter
{
    private readonly List<byte> _bytes = [];

    public ProtoWriter Varint(int field, long value)
    {
        Tag(field, 0);
        WriteVarint((ulong)value);
        return this;
    }

    public ProtoWriter Bool(int field, bool value) => Varint(field, value ? 1 : 0);

    public ProtoWriter Float(int field, float value)
    {
        Tag(field, 5);
        _bytes.AddRange(BitConverter.GetBytes(value));
        return this;
    }

    public ProtoWriter Fixed64(int field, ulong value)
    {
        Tag(field, 1);
        _bytes.AddRange(BitConverter.GetBytes(value));
        return this;
    }

    public ProtoWriter String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

    public ProtoWriter Message(int field, ProtoWriter inner) => Bytes(field, inner.ToArray());

    public ProtoWriter Packed(int field, params long[] values)
    {
        var inner = new ProtoWriter();
        foreach (var v in values)
        {
            inner.WriteVarint((ulong)v);
        }

        return Bytes(field, inner.ToArray());
    }

    public ProtoWriter Bytes(int field, byte[] value)
    {
        Tag(field, 2);
        WriteVarint((ulong)value.Length);
        _bytes.AddRange(value);
        return this;
    }

    public byte[] ToArray() => [.. _bytes];

    private void Tag(int field, int wire) => WriteVarint((ulong)((field << 3) | wire));

    private void WriteVarint(ulong value)
    {
        while (value >= 0x80)
        {
            _bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }

        _bytes.Add((byte)value);
    }
}
