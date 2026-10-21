using System.Text;
using Maki.Core.Download;

namespace Maki.Core.Tests;

public class TorrentInfoHashTests
{
    private const string V1Info = "d6:lengthi12345e4:name8:test.cbz12:piece lengthi16384e6:pieces20:aaaaaaaaaaaaaaaaaaaae";

    [Fact]
    public void A_torrent_file_hashes_its_info_dictionary_and_nothing_else()
    {
        var torrent = $"d8:announce15:http://t.test/a7:comment4:info4:info{V1Info}5:otherd4:infoi1eee";

        Assert.Equal("b0d01790a719342ff42de4fd924dea04a8286157",
            TorrentInfoHash.FromTorrentFile(Encoding.ASCII.GetBytes(torrent)));
    }

    [Fact]
    public void A_v2_only_torrent_takes_the_truncated_sha256_qbittorrent_lists()
    {
        const string info = "d9:file treed8:test.cbzd0:d6:lengthi1e11:pieces root32:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbeee" +
                            "12:meta versioni2e4:name8:test.cbz12:piece lengthi16384ee";

        Assert.Equal("31e7234d17675e86814060fb96825b1dec46ab77",
            TorrentInfoHash.FromTorrentFile(Encoding.ASCII.GetBytes($"d4:info{info}e")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("<html>Not found</html>")]
    [InlineData("d8:announce15:http://t.test/ae")]
    [InlineData("d4:infod4:name99:shorte")]
    [InlineData("d4:info2147483640:abce")]
    [InlineData("d2147483640:abce")]
    public void Anything_that_is_not_a_torrent_file_has_no_hash(string body)
    {
        Assert.Null(TorrentInfoHash.FromTorrentFile(Encoding.ASCII.GetBytes(body)));
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567&dn=x")]
    [InlineData("magnet:?dn=x&xt=urn:btih:AERUKZ4JVPG66AJDIVTYTK6N54ASGRLH")]
    public void A_magnet_hash_comes_out_as_the_lowercase_hex_qbittorrent_lists(string magnet)
    {
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", TorrentInfoHash.FromMagnet(magnet));
    }

    [Fact]
    public void A_magnet_without_a_btih_has_no_hash()
    {
        Assert.Null(TorrentInfoHash.FromMagnet("magnet:?xt=urn:btmh:1220abcd&dn=x"));
    }
}
