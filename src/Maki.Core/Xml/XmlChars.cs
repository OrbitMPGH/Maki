using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Maki.Core.Xml;

/// <summary>
/// XmlWriter refuses characters XML 1.0 cannot carry (most C0 controls, U+FFFE/FFFF, lone
/// surrogates), and provider overviews and titles do carry them. One stray U+0008 in an overview
/// used to fail every download of that series and every OPDS shelf that listed it.
/// </summary>
public static class XmlChars
{
    public static string? Strip(string? value)
    {
        if (string.IsNullOrEmpty(value) || FirstInvalid(value) < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (XmlConvert.IsXmlChar(c))
            {
                builder.Append(c);
            }
            else if (i + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[i + 1], c))
            {
                builder.Append(c).Append(value[i + 1]);
                i++;
            }
        }

        return builder.ToString();
    }

    /// <summary>Strips every text node and attribute value in <paramref name="document"/> in place.</summary>
    public static void Strip(XDocument document)
    {
        foreach (var text in document.DescendantNodes().OfType<XText>())
        {
            if (FirstInvalid(text.Value) >= 0) text.Value = Strip(text.Value)!;
        }

        foreach (var attribute in document.Descendants().Attributes())
        {
            if (FirstInvalid(attribute.Value) >= 0) attribute.Value = Strip(attribute.Value)!;
        }
    }

    private static int FirstInvalid(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (XmlConvert.IsXmlChar(value[i])) continue;
            if (i + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[i + 1], value[i]))
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }
}
