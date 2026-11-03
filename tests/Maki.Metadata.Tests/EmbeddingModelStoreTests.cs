using Maki.Metadata.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// A model change leaves the previous model's folder in the models directory, which also holds the
/// upscaler's models; only the listed retired folders may go.
/// </summary>
public class EmbeddingModelStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "maki-models-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Folder(string name)
    {
        var path = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        File.WriteAllText(Path.Combine(path, "model.onnx"), "x");
        return path;
    }

    private EmbeddingModelStore Store(EmbeddingModelProfile profile) =>
        new(new FakeDumpHttpClientFactory([]), new EmbeddingOptions(_root, Path.Combine(_root, "e.db"), _root, profile),
            NullLogger<EmbeddingModelStore>.Instance);

    [Fact]
    public void Retired_folders_go_and_everything_else_stays()
    {
        var retired = EmbeddingModelProfile.RetiredFolderNames
            .Where(n => !string.Equals(n, EmbeddingModelProfile.Base.FolderName, StringComparison.OrdinalIgnoreCase))
            .Select(Folder)
            .ToList();
        var current = Folder(EmbeddingModelProfile.Base.FolderName);
        var upscale = Folder("upscale");
        var unknown = Folder("something-else");

        Store(EmbeddingModelProfile.Base).RemoveRetiredModels();

        Assert.NotEmpty(retired);
        Assert.All(retired, path => Assert.False(Directory.Exists(path)));
        Assert.True(Directory.Exists(current));
        Assert.True(Directory.Exists(upscale));
        Assert.True(Directory.Exists(unknown));
    }

    [Fact]
    public void A_candidate_profile_never_removes_anything()
    {
        var retired = Folder("bge-base-en-v1.5");

        Store(EmbeddingModelProfile.Candidates["arctic-m2"]).RemoveRetiredModels();

        Assert.True(Directory.Exists(retired));
    }
}
