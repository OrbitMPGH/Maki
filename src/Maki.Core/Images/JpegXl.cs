using System.Reflection;
using System.Runtime.InteropServices;

namespace Maki.Core.Images;

/// <summary>
/// libjxl, called directly: the libvips Maki bundles has no JPEG XL. The server upscaler writes colour
/// pages with it and reads them back only to serve WebP to a browser that cannot decode JPEG XL.
/// The library ships beside the app (<c>distribution/libjxl</c>); without it <see cref="IsAvailable"/>
/// is false and every caller stays on WebP.
/// </summary>
public static class JpegXl
{
    private const string Library = "jxl";

    // Every field of JxlBasicInfo is four bytes, so the struct is passed as ints at these indices.
    private const int BasicInfoInts = 51;
    private const int InfoXSize = 1;
    private const int InfoYSize = 2;
    private const int InfoBitsPerSample = 3;
    private const int InfoUsesOriginalProfile = 9;
    private const int InfoColorChannels = 13;
    private const int InfoExtraChannels = 14;

    private const int EncSuccess = 0;
    private const int EncNeedMoreOutput = 2;
    private const int FrameSettingEffort = 0;

    private const int DecSuccess = 0;
    private const int DecNeedImageOutBuffer = 5;
    private const int DecBasicInfo = 0x40;
    private const int DecFullImage = 0x1000;

    private const int TypeUint8 = 2;

    /// <summary>The oldest libjxl this was measured with is 0.12; 0.11 has the same API.</summary>
    private const uint MinVersion = 11_000;

    private static readonly Lazy<uint> LoadedVersion = new(Probe);

    static JpegXl() => NativeLibrary.SetDllImportResolver(typeof(JpegXl).Assembly, Resolve);

    public static bool IsAvailable => LoadedVersion.Value >= MinVersion;

    /// <summary>"0.12.0", or null when libjxl is missing or older than 0.11.</summary>
    public static string? Version => IsAvailable
        ? $"{LoadedVersion.Value / 1_000_000}.{LoadedVersion.Value / 1000 % 1000}.{LoadedVersion.Value % 1000}"
        : null;

    /// <summary>
    /// A lossy JPEG XL codestream from interleaved 8-bit sRGB (<paramref name="bands"/> 3) or grey (1),
    /// on one thread. <paramref name="distance"/> is libjxl's Butteraugli distance (1 is visually
    /// lossless), <paramref name="effort"/> 1 to 10.
    /// </summary>
    public static byte[] Encode(byte[] pixels, int width, int height, int bands, float distance, int effort)
    {
        if (bands is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(bands), bands, "1 or 3");
        if ((long)width * height * bands > pixels.Length) throw new ArgumentException("Fewer pixels than the size says.", nameof(pixels));
        EnsureAvailable();

        var encoder = JxlEncoderCreate(IntPtr.Zero);
        if (encoder == IntPtr.Zero) throw new JpegXlException("JxlEncoderCreate");
        try
        {
            var info = new int[BasicInfoInts];
            JxlEncoderInitBasicInfo(info);
            info[InfoXSize] = width;
            info[InfoYSize] = height;
            info[InfoBitsPerSample] = 8;
            info[InfoColorChannels] = bands;
            info[InfoExtraChannels] = 0;
            info[InfoUsesOriginalProfile] = 0;
            Check(JxlEncoderSetBasicInfo(encoder, info), "JxlEncoderSetBasicInfo");

            var colour = new byte[ColorEncodingBytes];
            JxlColorEncodingSetToSRGB(colour, bands == 1 ? 1 : 0);
            Check(JxlEncoderSetColorEncoding(encoder, colour), "JxlEncoderSetColorEncoding");

            var settings = JxlEncoderFrameSettingsCreate(encoder, IntPtr.Zero);
            if (settings == IntPtr.Zero) throw new JpegXlException("JxlEncoderFrameSettingsCreate");
            Check(JxlEncoderSetFrameDistance(settings, distance), "JxlEncoderSetFrameDistance");
            Check(JxlEncoderFrameSettingsSetOption(settings, FrameSettingEffort, effort), "effort");

            var format = new PixelFormat((uint)bands, TypeUint8, 0, 0);
            Check(JxlEncoderAddImageFrame(settings, ref format, pixels, (nuint)((long)width * height * bands)), "JxlEncoderAddImageFrame");
            JxlEncoderCloseInput(encoder);
            return Drain(encoder, Math.Max(64 * 1024, width * height / 4));
        }
        finally
        {
            JxlEncoderDestroy(encoder);
        }
    }

    /// <summary>
    /// Decodes a JPEG XL image to interleaved 8-bit pixels in its own colour channels (1 or 3, alpha
    /// dropped). Throws <see cref="JpegXlException"/> for anything larger than <paramref name="maxPixels"/>.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height, int Bands) Decode(byte[] data, long maxPixels)
    {
        EnsureAvailable();
        var decoder = JxlDecoderCreate(IntPtr.Zero);
        if (decoder == IntPtr.Zero) throw new JpegXlException("JxlDecoderCreate");
        var input = GCHandle.Alloc(data, GCHandleType.Pinned);
        GCHandle output = default;
        try
        {
            CheckDec(JxlDecoderSubscribeEvents(decoder, DecBasicInfo | DecFullImage), "JxlDecoderSubscribeEvents");
            CheckDec(JxlDecoderSetInput(decoder, input.AddrOfPinnedObject(), (nuint)data.Length), "JxlDecoderSetInput");
            JxlDecoderCloseInput(decoder);

            byte[]? pixels = null;
            int width = 0, height = 0, bands = 0;
            var format = default(PixelFormat);
            while (true)
            {
                var status = JxlDecoderProcessInput(decoder);
                switch (status)
                {
                    case DecBasicInfo:
                        var info = new int[BasicInfoInts];
                        CheckDec(JxlDecoderGetBasicInfo(decoder, info), "JxlDecoderGetBasicInfo");
                        width = info[InfoXSize];
                        height = info[InfoYSize];
                        bands = info[InfoColorChannels] == 1 ? 1 : 3;
                        if (width <= 0 || height <= 0 || (long)width * height > maxPixels)
                        {
                            throw new JpegXlException($"{width}x{height} is over the size limit");
                        }

                        format = new PixelFormat((uint)bands, TypeUint8, 0, 0);
                        break;
                    case DecNeedImageOutBuffer:
                        CheckDec(JxlDecoderImageOutBufferSize(decoder, ref format, out var size), "JxlDecoderImageOutBufferSize");
                        if ((long)size != (long)width * height * bands) throw new JpegXlException("unexpected output size");
                        pixels = new byte[(int)size];
                        output = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                        CheckDec(JxlDecoderSetImageOutBuffer(decoder, ref format, output.AddrOfPinnedObject(), size), "JxlDecoderSetImageOutBuffer");
                        break;
                    case DecFullImage:
                        break;
                    case DecSuccess:
                        return pixels is null ? throw new JpegXlException("no image") : (pixels, width, height, bands);
                    default:
                        throw new JpegXlException($"JxlDecoderProcessInput returned {status}");
                }
            }
        }
        finally
        {
            if (output.IsAllocated) output.Free();
            input.Free();
            JxlDecoderDestroy(decoder);
        }
    }

    private static byte[] Drain(IntPtr encoder, int initial)
    {
        var buffer = new byte[initial];
        var written = 0;
        while (true)
        {
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            int status;
            try
            {
                var start = handle.AddrOfPinnedObject() + written;
                var next = start;
                var avail = (nuint)(buffer.Length - written);
                status = JxlEncoderProcessOutput(encoder, ref next, ref avail);
                written += (int)(next - start);
            }
            finally
            {
                handle.Free();
            }

            if (status == EncSuccess) return buffer.AsSpan(0, written).ToArray();
            if (status != EncNeedMoreOutput) throw new JpegXlException($"JxlEncoderProcessOutput returned {status}");
            Array.Resize(ref buffer, buffer.Length * 2);
        }
    }

    /// <summary>Larger than JxlColorEncoding (five enums and nine doubles, about 104 bytes).</summary>
    private const int ColorEncodingBytes = 256;

    private static void Check(int status, string call)
    {
        if (status != EncSuccess) throw new JpegXlException($"{call} returned {status}");
    }

    private static void CheckDec(int status, string call)
    {
        if (status != DecSuccess) throw new JpegXlException($"{call} returned {status}");
    }

    private static void EnsureAvailable()
    {
        if (!IsAvailable) throw new JpegXlException("libjxl 0.11 or newer is not installed beside Maki");
    }

    private static uint Probe()
    {
        try
        {
            return JxlEncoderVersion() == JxlDecoderVersion() ? JxlEncoderVersion() : 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return 0;
        }
    }

    /// <summary>The file names libjxl's builds give the library, looked for beside the app only.</summary>
    private static readonly string[] FileNames = ["jxl.dll", "libjxl.so.0.12", "libjxl.so.0.11", "libjxl.0.12.dylib", "libjxl.0.11.dylib"];

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return IntPtr.Zero;
        foreach (var file in FileNames)
        {
            var path = Path.Combine(AppContext.BaseDirectory, file);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle)) return handle;
        }

        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PixelFormat(uint NumChannels, int DataType, int Endianness, nuint Align);

    [DllImport(Library)] private static extern uint JxlEncoderVersion();
    [DllImport(Library)] private static extern uint JxlDecoderVersion();
    [DllImport(Library)] private static extern IntPtr JxlEncoderCreate(IntPtr memoryManager);
    [DllImport(Library)] private static extern void JxlEncoderDestroy(IntPtr encoder);
    [DllImport(Library)] private static extern void JxlEncoderInitBasicInfo([Out] int[] info);
    [DllImport(Library)] private static extern int JxlEncoderSetBasicInfo(IntPtr encoder, [In] int[] info);
    [DllImport(Library)] private static extern void JxlColorEncodingSetToSRGB([Out] byte[] colour, int isGrey);
    [DllImport(Library)] private static extern int JxlEncoderSetColorEncoding(IntPtr encoder, [In] byte[] colour);
    [DllImport(Library)] private static extern IntPtr JxlEncoderFrameSettingsCreate(IntPtr encoder, IntPtr source);
    [DllImport(Library)] private static extern int JxlEncoderSetFrameDistance(IntPtr settings, float distance);
    [DllImport(Library)] private static extern int JxlEncoderFrameSettingsSetOption(IntPtr settings, int option, long value);
    [DllImport(Library)] private static extern int JxlEncoderAddImageFrame(IntPtr settings, ref PixelFormat format, [In] byte[] buffer, nuint size);
    [DllImport(Library)] private static extern void JxlEncoderCloseInput(IntPtr encoder);
    [DllImport(Library)] private static extern int JxlEncoderProcessOutput(IntPtr encoder, ref IntPtr nextOut, ref nuint availOut);
    [DllImport(Library)] private static extern IntPtr JxlDecoderCreate(IntPtr memoryManager);
    [DllImport(Library)] private static extern void JxlDecoderDestroy(IntPtr decoder);
    [DllImport(Library)] private static extern int JxlDecoderSubscribeEvents(IntPtr decoder, int events);
    [DllImport(Library)] private static extern int JxlDecoderSetInput(IntPtr decoder, IntPtr data, nuint size);
    [DllImport(Library)] private static extern void JxlDecoderCloseInput(IntPtr decoder);
    [DllImport(Library)] private static extern int JxlDecoderProcessInput(IntPtr decoder);
    [DllImport(Library)] private static extern int JxlDecoderGetBasicInfo(IntPtr decoder, [Out] int[] info);
    [DllImport(Library)] private static extern int JxlDecoderImageOutBufferSize(IntPtr decoder, ref PixelFormat format, out nuint size);
    [DllImport(Library)] private static extern int JxlDecoderSetImageOutBuffer(IntPtr decoder, ref PixelFormat format, IntPtr buffer, nuint size);
}

public sealed class JpegXlException(string message) : Exception(message);
