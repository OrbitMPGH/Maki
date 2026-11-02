using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Maki.Api.Tests.Upscaling;

public class IdentityFixtureModelTests
{
    [Fact]
    public void The_identity_model_doubles_a_4x4_image_by_nearest_neighbour()
    {
        using var session = new InferenceSession(UpscaleFixture.IdentityModelPath);
        var input = new DenseTensor<float>([1, 3, 4, 4]);
        for (var c = 0; c < 3; c++)
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++)
        {
            input[0, c, y, x] = c * 100 + y * 4 + x;
        }

        using var results = session.Run([NamedOnnxValue.CreateFromTensor("input", input)]);
        var output = results.Single().AsTensor<float>();

        Assert.Equal([1, 3, 8, 8], output.Dimensions.ToArray());
        for (var c = 0; c < 3; c++)
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
        {
            Assert.Equal(input[0, c, y / 2, x / 2], output[0, c, y, x]);
        }
    }
}
