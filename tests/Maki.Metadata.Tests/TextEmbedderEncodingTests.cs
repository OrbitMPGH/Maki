using Maki.Metadata.Embedding;
using Microsoft.ML.Tokenizers;

namespace Maki.Metadata.Tests;

/// <summary>
/// Guards the special-token binding in <see cref="TextEmbedder.Encode(Tokenizer, string)"/>.
///
/// This exists because the bug it catches is invisible at every layer that would normally notice
/// one. <see cref="BertTokenizer"/> hides the base <c>Tokenizer.EncodeToIds(string, ...)</c> with an
/// overload whose second parameter is <c>addSpecialTokens</c>, so calling through a
/// <see cref="Tokenizer"/>-typed reference silently binds to the base method and drops [CLS]/[SEP].
/// Nothing throws: the model loads, embeds, normalizes, and returns unit vectors of the right width
/// that simply do not rank. When this was live, bge-base scored 0 of 12 on the hand-labelled query
/// set it had previously scored 9 of 12 on, with the identical text and the identical graph.
/// </summary>
public class TextEmbedderEncodingTests
{
    private const int Cls = 101;
    private const int Sep = 102;

    /// <summary>
    /// A minimal WordPiece vocabulary. The four specials must occupy the same ids BERT gives them
    /// ([PAD] 0 … [CLS] 101, [SEP] 102), so the filler below pads the gap rather than listing words.
    /// </summary>
    private static string WriteVocab()
    {
        var path = Path.Combine(Path.GetTempPath(), $"maki-vocab-{Guid.NewGuid():N}.txt");
        // BertTokenizer.Create refuses a vocabulary missing any of its five specials, so all of them
        // are present and at BERT's real ids: [PAD] 0, [UNK] 100, [CLS] 101, [SEP] 102, [MASK] 103.
        var lines = new List<string> { "[PAD]" };
        while (lines.Count < 100)
        {
            lines.Add($"[unused{lines.Count}]");
        }

        lines.Add("[UNK]");
        lines.Add("[CLS]");
        lines.Add("[SEP]");
        lines.Add("[MASK]");
        lines.AddRange(["boxing", "manga", "under", "##dog"]);
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void WordPieceEncodingKeepsTheClsAndSepSentinels()
    {
        var path = WriteVocab();
        try
        {
            var tokenizer = BertTokenizer.Create(path);
            var ids = TextEmbedder.Encode(tokenizer, "boxing manga");

            Assert.Equal(Cls, ids[0]);
            Assert.Equal(Sep, ids[^1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The trap itself, asserted directly: the base-typed call really does return something
    /// different. If a future version of Microsoft.ML.Tokenizers makes the two agree, this fails and
    /// says so, rather than leaving a cast in place that everyone assumes is redundant.
    /// </summary>
    [Fact]
    public void TheBaseTypedCallDropsThemWhichIsWhyEncodeCastsFirst()
    {
        var path = WriteVocab();
        try
        {
            var tokenizer = BertTokenizer.Create(path);
            var viaBase = ((Tokenizer)tokenizer).EncodeToIds("boxing manga");

            Assert.DoesNotContain(Cls, viaBase);
            Assert.DoesNotContain(Sep, viaBase);
            Assert.Equal(viaBase.Count + 2, TextEmbedder.Encode(tokenizer, "boxing manga").Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
