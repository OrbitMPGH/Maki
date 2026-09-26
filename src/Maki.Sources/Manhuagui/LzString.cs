using System.Text;

namespace Maki.Sources.Manhuagui;

/// <summary>
/// Hand-written port of pieroxy/lz-string's <c>decompressFromBase64</c>, the only entry point
/// manhuagui's packed chapter script needs (the site's own <c>String.prototype.splic</c> calls it
/// on the packer's <c>k</c> argument before splitting on <c>|</c>). No NuGet package exposes just
/// this one function without dragging in the rest of the compression suite, so it is reproduced
/// here from the reference algorithm.
/// </summary>
public static class LzString
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=";

    public static string DecompressFromBase64(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "";
        }

        var reader = new BitReader(input);

        var dictionary = new List<string> { "", "", "" };
        var enlargeIn = 4;
        var numBits = 3;

        char first;
        switch (reader.ReadBits(2))
        {
            case 0:
                first = (char)reader.ReadBits(8);
                break;
            case 1:
                first = (char)reader.ReadBits(16);
                break;
            default:
                return "";
        }

        dictionary.Add(first.ToString());
        var w = first.ToString();
        var result = new StringBuilder(w);

        while (true)
        {
            if (reader.Exhausted)
            {
                throw new InvalidDataException("manhuagui: truncated lz-string payload");
            }

            var cc = reader.ReadBits(numBits);
            switch (cc)
            {
                case 0:
                    dictionary.Add(((char)reader.ReadBits(8)).ToString());
                    cc = dictionary.Count - 1;
                    enlargeIn--;
                    break;
                case 1:
                    dictionary.Add(((char)reader.ReadBits(16)).ToString());
                    cc = dictionary.Count - 1;
                    enlargeIn--;
                    break;
                case 2:
                    return result.ToString();
            }

            if (enlargeIn == 0)
            {
                enlargeIn = 1 << numBits;
                numBits++;
            }

            string entry;
            if (cc < dictionary.Count)
            {
                entry = dictionary[cc];
            }
            else if (cc == dictionary.Count)
            {
                entry = w + w[0];
            }
            else
            {
                throw new InvalidDataException($"manhuagui: lz-string dictionary index {cc} out of range");
            }

            result.Append(entry);
            dictionary.Add(w + entry[0]);
            enlargeIn--;
            w = entry;

            if (enlargeIn == 0)
            {
                enlargeIn = 1 << numBits;
                numBits++;
            }
        }
    }

    /// <summary>Reads the base-64 payload <paramref name="input"/> bit by bit, 6 bits per character.</summary>
    private sealed class BitReader
    {
        private readonly int[] _vals;
        private int _val;
        private int _position = 32;
        private int _index = 1;

        public BitReader(string input)
        {
            _vals = new int[input.Length];
            for (var i = 0; i < input.Length; i++)
            {
                _vals[i] = Alphabet.IndexOf(input[i]);
            }

            _val = _vals[0];
        }

        public bool Exhausted => _index > _vals.Length;

        public int ReadBits(int n)
        {
            var bits = 0;
            var power = 1;
            var maxPower = 1 << n;
            while (power != maxPower)
            {
                var resb = _val & _position;
                _position >>= 1;
                if (_position == 0)
                {
                    _position = 32;
                    _val = _index < _vals.Length ? _vals[_index] : 0;
                    _index++;
                }

                if (resb > 0)
                {
                    bits |= power;
                }

                power <<= 1;
            }

            return bits;
        }
    }
}
