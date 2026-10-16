using System.Buffers.Binary;

namespace Maki.Core.Mihon;

/// <summary>
/// Minimal protobuf wire reader over one message. Wire types 0, 1, 2 and 5 only; anything else
/// (groups, reserved types) or a truncated value is a corrupt backup.
/// </summary>
internal ref struct ProtoReader(ReadOnlySpan<byte> data)
{
    public const int Varint = 0;
    public const int Fixed64 = 1;
    public const int LengthDelimited = 2;
    public const int Fixed32 = 5;

    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos;

    public readonly bool AtEnd => _pos >= _data.Length;

    public bool Next(out int field, out int wire)
    {
        if (AtEnd)
        {
            field = wire = 0;
            return false;
        }

        var tag = ReadVarint();
        var number = tag >> 3;
        if (number is 0 or > int.MaxValue)
        {
            throw Corrupt("invalid field number");
        }

        field = (int)number;
        wire = (int)(tag & 7);
        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (_pos >= _data.Length)
            {
                throw Corrupt("truncated varint");
            }

            var b = _data[_pos++];
            if (shift < 64)
            {
                result |= (ulong)(b & 0x7F) << shift;
            }

            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw Corrupt("varint too long");
    }

    public uint ReadFixed32()
    {
        if (_data.Length - _pos < 4)
        {
            throw Corrupt("truncated fixed32");
        }

        var value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_pos..]);
        _pos += 4;
        return value;
    }

    public ulong ReadFixed64()
    {
        if (_data.Length - _pos < 8)
        {
            throw Corrupt("truncated fixed64");
        }

        var value = BinaryPrimitives.ReadUInt64LittleEndian(_data[_pos..]);
        _pos += 8;
        return value;
    }

    public float ReadFloat() => BitConverter.UInt32BitsToSingle(ReadFixed32());

    public ReadOnlySpan<byte> ReadBytes()
    {
        var (offset, length) = ReadRange();
        return _data.Slice(offset, length);
    }

    public (int Offset, int Length) ReadRange()
    {
        var length = ReadVarint();
        if (length > (ulong)(_data.Length - _pos))
        {
            throw Corrupt("length runs past the end");
        }

        var offset = _pos;
        _pos += (int)length;
        return (offset, (int)length);
    }

    public void Skip(int wire)
    {
        switch (wire)
        {
            case Varint:
                ReadVarint();
                break;
            case Fixed64:
                ReadFixed64();
                break;
            case LengthDelimited:
                ReadBytes();
                break;
            case Fixed32:
                ReadFixed32();
                break;
            default:
                throw Corrupt("unsupported wire type");
        }
    }

    private static MihonBackupException Corrupt(string detail) =>
        new(MihonBackupFailure.Corrupt, $"Malformed protobuf: {detail}");
}
