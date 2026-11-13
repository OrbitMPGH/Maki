using System.Text;
using System.Xml.Linq;
using Maki.Core.Images;

namespace Maki.Core.Tests;

public class ComicInfoSplitPagesTests
{
    private static XElement Rewrite(string pages, Dictionary<int, int> pieces, int pageCount)
    {
        var xml = $"<ComicInfo><PageCount>3</PageCount><Pages>{pages}</Pages></ComicInfo>";
        var bytes = ArchivePageConverter.WithPageCount(Encoding.UTF8.GetBytes(xml), pageCount, pieces);
        return XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!;
    }

    [Fact]
    public void A_split_page_becomes_several_entries_and_later_pages_shift()
    {
        var root = Rewrite(
            """<Page Image="0" Type="FrontCover" /><Page Image="1" ImageHeight="30000" DoublePage="true" /><Page Image="2" />""",
            new() { [1] = 3 }, 5);

        Assert.Equal("5", root.Element("PageCount")!.Value);
        var pages = root.Element("Pages")!.Elements("Page").ToList();
        Assert.Equal(["0", "1", "2", "3", "4"], pages.Select(p => (string)p.Attribute("Image")!));
        Assert.Equal("FrontCover", (string?)pages[0].Attribute("Type"));
        Assert.Null(pages[1].Attribute("ImageHeight"));
        Assert.Null(pages[1].Attribute("DoublePage"));
        Assert.Single(pages[2].Attributes());
    }

    [Fact]
    public void A_block_with_an_unreadable_index_is_dropped()
    {
        var root = Rewrite("""<Page Image="cover" /><Page Image="1" />""", new() { [1] = 2 }, 3);

        Assert.Null(root.Element("Pages"));
        Assert.Equal("3", root.Element("PageCount")!.Value);
    }
}
