using System.Text;
using Maki.Metadata.Embedding;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Maki.Metadata.Tests;

/// <summary>
/// The external-data rewrite on a small hand-built graph (two MatMuls and an Add, so one weight
/// stays inline and two move), and the pair's on-disk life: built once, rebuilt when the stamp,
/// the data file or the source no longer agree.
/// </summary>
public class MappedEmbeddingModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"maki-mapped-model-{Guid.NewGuid():N}");
    private readonly float[] _w1 = Weights(64 * 64, 1);
    private readonly float[] _w2 = Weights(64 * 32, 2);
    private readonly float[] _bias = Weights(32, 3);

    public MappedEmbeddingModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string ModelPath => Path.Combine(_root, "model.onnx");

    private MappedEmbeddingModel WriteModel()
    {
        File.WriteAllBytes(ModelPath, TinyModel(_w1, _w2, _bias));
        return new MappedEmbeddingModel(ModelPath);
    }

    /// <summary>
    /// Prepacking off on both sides: for float MatMul it changes the summation order, which is not
    /// what is under test here. The int8 kernels the embedder runs come out identical either way.
    /// </summary>
    [Fact]
    public void The_mapped_pair_computes_exactly_what_the_plain_model_does()
    {
        var mapped = WriteModel();
        mapped.Build();

        var input = Weights(64, 9);
        var plain = Run(ModelPath, input, disablePrepacking: true);
        var external = Run(mapped.GraphPath, input, disablePrepacking: true);

        Assert.Equal(32, plain.Length);
        Assert.Equal(plain, external);
    }

    [Fact]
    public void Large_initializers_move_to_aligned_offsets_and_small_ones_stay_inline()
    {
        var mapped = WriteModel();
        mapped.Build();

        var data = File.ReadAllBytes(mapped.DataPath);
        Assert.Equal(Bytes(_w1), data[..(_w1.Length * 4)]);
        Assert.Equal(Bytes(_w2), data[OnnxExternalData.Alignment..(OnnxExternalData.Alignment + (_w2.Length * 4))]);
        Assert.Equal(OnnxExternalData.Alignment + (_w2.Length * 4), data.Length);

        var graph = File.ReadAllBytes(mapped.GraphPath);
        Assert.True(graph.Length < 2048);
        Assert.Contains("model.mapped.onnx_data", Encoding.UTF8.GetString(graph));
    }

    [Fact]
    public void A_built_pair_is_current_and_is_not_rebuilt()
    {
        var mapped = WriteModel();
        Assert.False(mapped.IsCurrent());

        mapped.Build();
        var written = File.GetLastWriteTimeUtc(mapped.DataPath);

        Assert.True(mapped.IsCurrent());
        Assert.True(new MappedEmbeddingModel(ModelPath).IsCurrent());
        Assert.Equal(written, File.GetLastWriteTimeUtc(mapped.DataPath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void A_truncated_or_missing_data_file_is_not_current_and_rebuilds()
    {
        var mapped = WriteModel();
        mapped.Build();

        using (var data = new FileStream(mapped.DataPath, FileMode.Open))
        {
            data.SetLength(data.Length / 2);
        }

        Assert.False(mapped.IsCurrent());
        mapped.Build();
        Assert.True(mapped.IsCurrent());

        File.Delete(mapped.DataPath);
        Assert.False(mapped.IsCurrent());
        mapped.Build();
        Assert.True(mapped.IsCurrent());
        Assert.Equal(Run(ModelPath, Weights(64, 4), true), Run(mapped.GraphPath, Weights(64, 4), true));
    }

    [Fact]
    public void A_pair_without_its_stamp_is_not_current()
    {
        var mapped = WriteModel();
        mapped.Build();
        File.Delete(mapped.StampPath);

        Assert.False(mapped.IsCurrent());
    }

    [Fact]
    public void A_new_download_of_the_model_makes_the_pair_stale()
    {
        var mapped = WriteModel();
        mapped.Build();

        File.WriteAllBytes(ModelPath, TinyModel(Weights(64 * 64, 7), _w2, _bias));
        File.SetLastWriteTimeUtc(ModelPath, DateTime.UtcNow.AddMinutes(1));

        Assert.False(mapped.IsCurrent());
    }

    [Fact]
    public void A_model_that_does_not_parse_leaves_nothing_behind()
    {
        var garbage = new byte[200_000];
        new Random(5).NextBytes(garbage);
        File.WriteAllBytes(ModelPath, garbage);
        var mapped = new MappedEmbeddingModel(ModelPath);

        Assert.ThrowsAny<Exception>(mapped.Build);

        Assert.Equal(["model.onnx"], Directory.GetFiles(_root).Select(Path.GetFileName));
        Assert.False(mapped.IsCurrent());
    }

    private static float[] Run(string path, float[] input, bool disablePrepacking)
    {
        using var options = OnnxMemoryOptions.Create(1, fixedShapes: false);
        if (disablePrepacking)
        {
            options.AddSessionConfigEntry("session.disable_prepacking", "1");
        }

        using var session = new InferenceSession(path, options);
        var x = new DenseTensor<float>(input, [1, 64]);
        using var results = session.Run([NamedOnnxValue.CreateFromTensor("x", x)]);
        return results.First().AsTensor<float>().ToArray();
    }

    private static float[] Weights(int count, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();
    }

    private static byte[] Bytes(float[] values)
    {
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>y = (x · W1) · W2 + B, x [1,64], W1 [64,64], W2 [64,32], B [32], written as ONNX protobuf by hand.</summary>
    private static byte[] TinyModel(float[] w1, float[] w2, float[] bias)
    {
        var graph = new Proto()
            .Message(1, new Proto().Text(1, "x").Text(1, "W1").Text(2, "h").Text(4, "MatMul"))
            .Message(1, new Proto().Text(1, "h").Text(1, "W2").Text(2, "m").Text(4, "MatMul"))
            .Message(1, new Proto().Text(1, "m").Text(1, "B").Text(2, "y").Text(4, "Add"))
            .Text(2, "tiny")
            .Message(5, Tensor("W1", w1, 64, 64))
            .Message(5, Tensor("W2", w2, 64, 32))
            .Message(5, Tensor("B", bias, 32))
            .Message(11, ValueInfo("x", 1, 64))
            .Message(12, ValueInfo("y", 1, 32));

        return new Proto()
            .Varint(1, 8)
            .Message(8, new Proto().Varint(2, 13))
            .Message(7, graph)
            .ToArray();
    }

    private static Proto Tensor(string name, float[] values, params long[] dims)
    {
        var tensor = new Proto();
        foreach (var dim in dims)
        {
            tensor.Varint(1, (ulong)dim);
        }

        return tensor.Varint(2, 1).Text(8, name).Bytes(9, Bytes(values));
    }

    private static Proto ValueInfo(string name, params long[] dims)
    {
        var shape = new Proto();
        foreach (var dim in dims)
        {
            shape.Message(1, new Proto().Varint(1, (ulong)dim));
        }

        var tensorType = new Proto().Varint(1, 1).Message(2, shape);
        return new Proto().Text(1, name).Message(2, new Proto().Message(1, tensorType));
    }

    private sealed class Proto
    {
        private readonly MemoryStream _buffer = new();

        public Proto Varint(int field, ulong value)
        {
            Write(((ulong)field << 3) | 0);
            Write(value);
            return this;
        }

        public Proto Bytes(int field, byte[] value)
        {
            Write(((ulong)field << 3) | 2);
            Write((ulong)value.Length);
            _buffer.Write(value);
            return this;
        }

        public Proto Text(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

        public Proto Message(int field, Proto value) => Bytes(field, value.ToArray());

        public byte[] ToArray() => _buffer.ToArray();

        private void Write(ulong value)
        {
            while (value >= 0x80)
            {
                _buffer.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            _buffer.WriteByte((byte)value);
        }
    }
}
