using System.Security.Cryptography;
using System.Text;
using Maki.Core.Reading;

namespace Maki.Core.Tests;

public class KoreaderDigestTests
{
    private static byte[] Bytes(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 31 + 7)).ToArray();

    private static string Md5(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));

    [Fact]
    public void A_file_under_one_kilobyte_hashes_whole()
    {
        var bytes = Bytes(700);

        Assert.Equal(Md5(bytes), KoreaderDigest.PartialMd5(new MemoryStream(bytes)));
    }

    [Fact]
    public void Samples_one_kilobyte_at_zero_then_at_quadrupling_offsets()
    {
        // Offsets 0, 1024, 4096, 16384, 65536; the next (262144) is past the end.
        var bytes = Bytes(70_000);
        var expected = new List<byte>();
        foreach (var offset in new[] { 0, 1024, 4096, 16384, 65536 })
        {
            expected.AddRange(bytes.Skip(offset).Take(1024));
        }

        Assert.Equal(Md5([.. expected]), KoreaderDigest.PartialMd5(new MemoryStream(bytes)));
    }

    [Fact]
    public void A_sample_cut_short_by_the_end_of_the_file_is_hashed_as_far_as_it_goes()
    {
        var bytes = Bytes(4500);
        byte[] expected = [.. bytes.Take(1024), .. bytes.Skip(1024).Take(1024), .. bytes.Skip(4096)];

        Assert.Equal(Md5(expected), KoreaderDigest.PartialMd5(new MemoryStream(bytes)));
    }

    [Fact]
    public void A_file_name_digest_is_the_md5_of_its_utf8_name()
    {
        Assert.Equal(Md5(Encoding.UTF8.GetBytes("Frieren - Ch. 12.cbz")), KoreaderDigest.FileName("Frieren - Ch. 12.cbz"));
    }

    [Fact]
    public void File_name_variants_include_the_fat_safe_spelling()
    {
        var variants = KoreaderDigest.FileNameVariants(["Re:Zero - Ch. 3?.cbz"]);

        Assert.Contains("Re:Zero - Ch. 3?.cbz", variants);
        Assert.Contains("Re_Zero - Ch. 3_.cbz", variants);
    }
}
