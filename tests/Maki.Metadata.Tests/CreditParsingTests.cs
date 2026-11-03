using System.Text.Json;
using Maki.Metadata.Catalogue;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// The credit build streams each column with Utf8JsonReader. These hold it to the answers the
/// JsonDocument version gave, kept below as the reference, on the shapes the dump has and the ones
/// it should not.
/// </summary>
public class CreditParsingTests
{
    public static TheoryData<string?> Inputs() =>
    [
        null, "", "   ", "[]", "[\"Kentaro Miura\"]", "[\"A\", \"B\", \"A\"]", "[\"  \", \"\", \"Real\"]",
        "[\"Esc\\u00e9 \\\"quoted\\\"\", \"tab\\tname\"]", "[\"日本語の名前\", \"Ōkubo\"]",
        "[1, true, null, \"Kept\", [\"nested\"], {\"name\": \"Obj\"}]",
        "{\"name\": \"not an array\"}", "\"bare string\"", "42",
        "[\"unterminated", "[\"a\",]", "[\"a\"] trailing", "[\"a\"]   ", "[\"a\"][\"b\"]", "[\"a\" \"b\"]",
        "[{\"name\": \"Shueisha\", \"note\": null, \"type\": \"Original\"}, \"Bare Publisher\"]",
        "[{\"name\": \"First\", \"name\": \"Last\"}]", "[{\"name\": \"First\", \"name\": 3}]",
        "[{\"name\": 3, \"name\": \"Last\"}]", "[{\"type\": \"Original\"}, {\"name\": {\"nested\": \"x\"}}]",
        "[{\"n\\u0061me\": \"Escaped key\"}]", "[{\"name\": \"   \"}, {\"name\": \"Kodansha\", \"extra\": [1, {\"name\": \"deep\"}]}]",
        "[{\"name\": \"broken\"", "[" + string.Join(",", Enumerable.Range(0, 400).Select(i => $"\"Name {i} with some length to pass the stack buffer\"")) + "]",
        new string('[', 70) + new string(']', 70),
    ];

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Names_match_the_document_parser(string? json) =>
        Assert.Equal(ReferenceNames(json), CreditIndex.ParseNames(json));

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Publishers_match_the_document_parser(string? json) =>
        Assert.Equal(ReferencePublishers(json), CreditIndex.ParsePublisherNames(json));

    private static List<string> ReferenceNames(string? json)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return names;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return names;
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String && element.GetString() is { } name &&
                    !string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }
        catch (JsonException)
        {
        }

        return names;
    }

    private static List<string> ReferencePublishers(string? json)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return names;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return names;
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var name = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Object when element.TryGetProperty("name", out var property)
                        && property.ValueKind == JsonValueKind.String => property.GetString(),
                    _ => null,
                };

                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }
        catch (JsonException)
        {
        }

        return names;
    }
}
