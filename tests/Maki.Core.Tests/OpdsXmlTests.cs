using System.Xml.Linq;
using Maki.Core.Opds;

namespace Maki.Core.Tests;

public class OpdsXmlTests
{
    [Fact]
    public void An_overview_with_control_characters_still_renders()
    {
        var updated = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = new OpdsEntry("urn:maki:series:1", "Berserk\u0007", updated,
            Content: "Dark\u0008 fantasy\u001F.", Author: "MIURA\u0001 Kentaro", Categories: ["action\u0002"]);
        var feed = new OpdsFeed("urn:maki:root", "Library", updated, OpdsFeedKind.Acquisition, [], [entry]);

        var xml = XDocument.Parse(OpdsXml.Render(feed));

        var rendered = xml.Root!.Element(OpdsXml.Atom + "entry")!;
        Assert.Equal("Berserk", rendered.Element(OpdsXml.Atom + "title")!.Value);
        Assert.Equal("Dark fantasy.", rendered.Element(OpdsXml.Atom + "content")!.Value);
        Assert.Equal("MIURA Kentaro", rendered.Element(OpdsXml.Atom + "author")!.Value);
        Assert.Equal("action", rendered.Element(OpdsXml.Atom + "category")!.Attribute("term")!.Value);
    }
}
