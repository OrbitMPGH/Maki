using System.Globalization;
using Maki.Core.Io;

namespace Maki.Metadata.Embedding;

/// <summary>
/// The memory-mapped form of a self-contained model: <c>model.mapped.onnx</c> (the graph) and
/// <c>model.mapped.onnx_data</c> (the weights at 64 KB-aligned offsets) beside the downloaded
/// <c>model.onnx</c>, plus a stamp that ties the pair to that file.
///
/// <para>
/// With the weights in an aligned external file and prepacking off, ONNX Runtime maps the file and
/// the int8 kernels read the mapped pages, so a loaded session holds about 100 MB less anonymous
/// memory and its weights are page cache the kernel can reclaim. Outputs are bit-identical.
/// </para>
///
/// <para>
/// Built from the download rather than shipped, because existing installs already hold the plain
/// file. The plain <c>model.onnx</c> stays: <see cref="EmbeddingModelStore"/> verifies and
/// re-downloads that file, and it is the fallback whenever the pair is missing or will not load.
/// Both files are written under temporary names and renamed into place, never written over, because
/// on Linux truncating a file a live session has mapped is a SIGBUS. The stamp is written last and
/// deleted first, so a crash at any point leaves a pair that <see cref="IsCurrent"/> rejects.
/// </para>
/// </summary>
internal sealed class MappedEmbeddingModel(string sourcePath)
{
    /// <summary>Bump when the layout changes, so every install rebuilds its pair.</summary>
    private const int Format = 1;

    private const string TempSuffix = ".tmp";

    public string SourcePath { get; } = sourcePath;

    public string GraphPath { get; } = Path.Combine(
        Path.GetDirectoryName(sourcePath) ?? ".", Path.GetFileNameWithoutExtension(sourcePath) + ".mapped.onnx");

    public string DataPath => GraphPath + "_data";

    public string StampPath => GraphPath + ".stamp";

    /// <summary>
    /// True when a complete pair built from the current source is on disk. Cheap: file sizes and
    /// the source's write time against the stamp, no hashing. A re-download moves a new file into
    /// place, which changes both.
    /// </summary>
    public bool IsCurrent()
    {
        try
        {
            if (!File.Exists(StampPath) || !File.Exists(SourcePath) ||
                !File.Exists(GraphPath) || !File.Exists(DataPath))
            {
                return false;
            }

            var stamp = Stamp(SourceIdentity(), new FileInfo(GraphPath).Length, new FileInfo(DataPath).Length);
            return string.Equals(File.ReadAllText(StampPath).Trim(), stamp, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private (long Length, long WriteTicks) SourceIdentity()
    {
        var source = new FileInfo(SourcePath);
        return (source.Length, source.LastWriteTimeUtc.Ticks);
    }

    private static string Stamp((long Length, long WriteTicks) source, long graphLength, long dataLength) =>
        string.Join(' ', new[] { Format, source.Length, source.WriteTicks, graphLength, dataLength }
            .Select(v => v.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Converts the source into a fresh pair and moves it into place. Throws on any failure, after
    /// removing whatever it had written; the caller keeps loading the plain model.
    /// </summary>
    public void Build()
    {
        var graphTemp = GraphPath + TempSuffix;
        var dataTemp = DataPath + TempSuffix;
        var stampTemp = StampPath + TempSuffix;
        try
        {
            var identity = SourceIdentity();
            using (var source = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024))
            using (var graph = new FileStream(graphTemp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
            using (var data = new FileStream(dataTemp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
            {
                OnnxExternalData.Rewrite(source, graph, data, Path.GetFileName(DataPath));
                graph.Flush(flushToDisk: true);
                data.Flush(flushToDisk: true);
            }

            if (SourceIdentity() != identity)
            {
                throw new IOException("The model changed while it was being converted");
            }

            var graphLength = new FileInfo(graphTemp).Length;
            var dataLength = new FileInfo(dataTemp).Length;
            File.Delete(StampPath);
            File.Move(dataTemp, DataPath, overwrite: true);
            File.Move(graphTemp, GraphPath, overwrite: true);
            using (var stamp = new FileStream(stampTemp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stamp))
            {
                writer.Write(Stamp(identity, graphLength, dataLength));
                writer.Flush();
                stamp.Flush(flushToDisk: true);
            }

            File.Move(stampTemp, StampPath, overwrite: true);
        }
        catch
        {
            TryDelete(graphTemp);
            TryDelete(dataTemp);
            TryDelete(stampTemp);
            throw;
        }
        finally
        {
            // Read once and written once; the next load maps the weights back in as it needs them.
            PageCache.DropAfterScan(SourcePath);
            PageCache.DropAfterScan(DataPath);
        }
    }

    /// <summary>Removes the pair. The stamp goes first, so a partial delete is never taken for current.</summary>
    public void Delete()
    {
        TryDelete(StampPath);
        TryDelete(GraphPath);
        TryDelete(DataPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
