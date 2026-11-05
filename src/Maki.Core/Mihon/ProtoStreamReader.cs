namespace Maki.Core.Mihon;

/// <summary>
/// The top level of a backup read straight off a stream: tags, varints and whole field values,
/// with each message handed out from one reused buffer for <see cref="ProtoReader"/> to parse.
/// Counts every byte it takes from the stream and stops at <c>maxBytes</c>, which on a gzipped
/// backup is the decompressed size.
/// </summary>
internal sealed class ProtoStreamReader(Stream stream, long maxBytes)
{
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _pos;
    private int _len;
    private long _taken;
    private byte[] _message = new byte[4096];

    /// <summary>The start of the stream, as much as fits in the buffer, without consuming it.</summary>
    public ReadOnlySpan<byte> Lookahead()
    {
        while (_len < _buffer.Length && Pull(_buffer.AsSpan(_len)) is > 0 and var read)
        {
            _len += read;
        }

        return _buffer.AsSpan(_pos, _len - _pos);
    }

    public bool Next(out int field, out int wire)
    {
        if (!Fill())
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

    /// <summary>A length-delimited value, valid until the next call.</summary>
    public ReadOnlySpan<byte> ReadMessage()
    {
        var length = ReadLength();
        var copied = 0;
        while (copied < length)
        {
            if (!Fill())
            {
                throw Corrupt("length runs past the end");
            }

            // Grown as the bytes arrive, so a bogus length in a small file never allocates it.
            var take = Math.Min(length - copied, _len - _pos);
            if (_message.Length < copied + take)
            {
                Array.Resize(ref _message, (int)Math.Min(length, Math.Max(copied + take, _message.Length * 2L)));
            }

            _buffer.AsSpan(_pos, take).CopyTo(_message.AsSpan(copied));
            _pos += take;
            copied += take;
        }

        return _message.AsSpan(0, length);
    }

    public void Skip(int wire)
    {
        switch (wire)
        {
            case ProtoReader.Varint:
                ReadVarint();
                break;
            case ProtoReader.Fixed64:
                SkipBytes(8, "truncated fixed64");
                break;
            case ProtoReader.LengthDelimited:
                SkipBytes(ReadLength(), "length runs past the end");
                break;
            case ProtoReader.Fixed32:
                SkipBytes(4, "truncated fixed32");
                break;
            default:
                throw Corrupt("unsupported wire type");
        }
    }

    private ulong ReadVarint()
    {
        ulong result = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (!Fill())
            {
                throw Corrupt("truncated varint");
            }

            var b = _buffer[_pos++];
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

    /// <summary>A length no backup within the cap could hold is a damaged one, as in <see cref="ProtoReader"/>.</summary>
    private int ReadLength()
    {
        var length = ReadVarint();
        if (length > (ulong)Math.Min(maxBytes, int.MaxValue))
        {
            throw Corrupt("length runs past the end");
        }

        return (int)length;
    }

    private void SkipBytes(long count, string truncated)
    {
        while (count > 0)
        {
            if (!Fill())
            {
                throw Corrupt(truncated);
            }

            var take = (int)Math.Min(count, _len - _pos);
            _pos += take;
            count -= take;
        }
    }

    private bool Fill()
    {
        if (_pos < _len)
        {
            return true;
        }

        _pos = 0;
        _len = Pull(_buffer);
        return _len > 0;
    }

    private int Pull(Span<byte> into)
    {
        int read;
        try
        {
            read = stream.Read(into);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            throw new MihonBackupException(MihonBackupFailure.Corrupt, "The gzip data is damaged.", e);
        }

        _taken += read;
        if (_taken > maxBytes)
        {
            throw new MihonBackupException(MihonBackupFailure.TooLarge, "The backup is too large.");
        }

        return read;
    }

    private static MihonBackupException Corrupt(string detail) =>
        new(MihonBackupFailure.Corrupt, $"Malformed protobuf: {detail}");
}
