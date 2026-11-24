using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Maki.Core.Images;

/// <summary>
/// libjxl, called directly: the libvips Maki bundles has no JPEG XL. The server upscaler writes colour
/// pages with it, page conversion can store pages in it, and <see cref="ImageCodec"/> decodes it
/// wherever a page is read.
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

        var encoder = CreateEncoder(width, height, bands, distance, effort, out var settings);
        try
        {
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

    /// <inheritdoc cref="Encode(byte[], int, int, int, float, int)"/>
    public static byte[] Encode(IntPtr pixels, long byteLength, int width, int height, int bands, float distance, int effort)
    {
        if (bands is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(bands), bands, "1 or 3");
        if ((long)width * height * bands > byteLength) throw new ArgumentException("Fewer pixels than the size says.", nameof(pixels));
        EnsureAvailable();

        var encoder = CreateEncoder(width, height, bands, distance, effort, out var settings);
        try
        {
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
    /// <see cref="Encode"/> with the pixels pulled from <paramref name="rows"/> as libjxl needs them, so the
    /// whole image never sits in one buffer, and libjxl keeps no copy of it either. libjxl asks on the
    /// calling thread, in rectangles of at most 2048 px a side plus an 8 px border (measured on 0.12: its
    /// 2048 px DC groups in reading order, each released before the next is asked for). The bytes are the
    /// same as <see cref="Encode"/>'s. An <see cref="IJpegXlRows.Acquire"/> that throws ends the encode
    /// with that exception.
    /// </summary>
    public static byte[] EncodeRows(IJpegXlRows rows, int width, int height, int bands, float distance, int effort)
    {
        if (bands is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(bands), bands, "1 or 3");
        EnsureAvailable();

        var encoder = CreateEncoder(width, height, bands, distance, effort, out var settings);
        RowStream? stream = null;
        var handle = default(GCHandle);
        try
        {
            stream = new RowStream(rows, width, bands, Math.Max(64 * 1024, width * height / 4));
            handle = GCHandle.Alloc(stream);
            var opaque = GCHandle.ToIntPtr(handle);
            // libjxl only streams the input when it can also stream the output.
            Check(JxlEncoderSetOutputProcessor(encoder, new OutputProcessor(opaque)), "JxlEncoderSetOutputProcessor");
            var status = JxlEncoderAddChunkedFrame(settings, 1, new ChunkedSource(opaque));
            stream.ThrowIfFailed();
            Check(status, "JxlEncoderAddChunkedFrame");
            status = JxlEncoderFlushInput(encoder);
            stream.ThrowIfFailed();
            Check(status, "JxlEncoderFlushInput");
            return stream.Encoded();
        }
        finally
        {
            JxlEncoderDestroy(encoder);
            if (handle.IsAllocated) handle.Free();
            stream?.Dispose();
        }
    }

    private static IntPtr CreateEncoder(int width, int height, int bands, float distance, int effort, out IntPtr settings)
    {
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

            settings = JxlEncoderFrameSettingsCreate(encoder, IntPtr.Zero);
            if (settings == IntPtr.Zero) throw new JpegXlException("JxlEncoderFrameSettingsCreate");
            Check(JxlEncoderSetFrameDistance(settings, distance), "JxlEncoderSetFrameDistance");
            Check(JxlEncoderFrameSettingsSetOption(settings, FrameSettingEffort, effort), "effort");
            return encoder;
        }
        catch
        {
            JxlEncoderDestroy(encoder);
            throw;
        }
    }

    /// <summary>
    /// One <see cref="EncodeRows"/>: the caller's rows, the buffers lent to libjxl, the codestream it writes
    /// (a seekable, growing native buffer) and the first failure. An exception that crossed back into libjxl
    /// would end the process, so every callback catches, keeps the failure here, and it is thrown once libjxl
    /// returns.
    /// </summary>
    private sealed class RowStream : IDisposable
    {
        private readonly IJpegXlRows _rows;
        private readonly int _bands;
        private readonly List<(IntPtr At, IntPtr Rows)> _lent = new(8);
        private readonly byte[] _blank;
        private Exception? _failure;
        private IntPtr _output;
        private long _capacity;
        private long _position;
        private long _length;

        public RowStream(IJpegXlRows rows, int width, int bands, int initialOutput)
        {
            _rows = rows;
            _bands = bands;
            _blank = GC.AllocateArray<byte>(width * bands, pinned: true);
            _output = Marshal.AllocHGlobal(initialOutput);
            _capacity = initialOutput;
        }

        public int Bands => _bands;

        public IntPtr Lend(int x, int y, int count, out nuint rowBytes)
        {
            if (_failure is null)
            {
                var start = IntPtr.Zero;
                try
                {
                    start = _rows.Acquire(y, count, out var stride);
                    var at = start + x * _bands;
                    _lent.Add((at, start));
                    rowBytes = (nuint)stride;
                    return at;
                }
                catch (Exception ex)
                {
                    _failure = ex;
                    if (start != IntPtr.Zero) ReleaseRows(start);
                }
            }

            return Blank(out rowBytes);
        }

        /// <summary>
        /// One zeroed row lent with a stride of 0, so every row of the request reads it and nothing is allocated
        /// on the way. libjxl cannot be stopped from an input callback, so after a failure it reads these until
        /// it next asks for output, which is refused.
        /// </summary>
        public IntPtr Blank(out nuint rowBytes)
        {
            rowBytes = 0;
            return Marshal.UnsafeAddrOfPinnedArrayElement(_blank, 0);
        }

        public void Return(IntPtr buffer)
        {
            var i = _lent.FindIndex(lent => lent.At == buffer);
            if (i < 0)
            {
                return;
            }

            var start = _lent[i].Rows;
            _lent.RemoveAt(i);
            ReleaseRows(start);
        }

        private void ReleaseRows(IntPtr start)
        {
            try
            {
                _rows.Release(start);
            }
            catch (Exception ex)
            {
                _failure ??= ex;
            }
        }

        /// <summary>libjxl holds at most one output buffer at a time, so growing here never moves one it is writing.</summary>
        public IntPtr OutputBuffer(ref nuint size)
        {
            if (_failure is not null)
            {
                size = 0;
                return IntPtr.Zero;
            }

            var wanted = _position + (long)Math.Max(size, 4096);
            if (wanted > _capacity)
            {
                var capacity = Math.Max(wanted, _capacity * 2);
                if (capacity > Array.MaxLength) throw new JpegXlException("The codestream does not fit in one buffer.");
                _output = Marshal.ReAllocHGlobal(_output, (IntPtr)capacity);
                _capacity = capacity;
            }

            size = (nuint)(_capacity - _position);
            return _output + (nint)_position;
        }

        public void Written(nuint bytes)
        {
            _position += (long)bytes;
            _length = Math.Max(_length, _position);
        }

        public void Seek(ulong position) => _position = (long)position;

        public void Fail(Exception ex) => _failure ??= ex;

        public void ThrowIfFailed()
        {
            if (_failure is not null) ExceptionDispatchInfo.Throw(_failure);
        }

        public byte[] Encoded()
        {
            var bytes = new byte[_length];
            Marshal.Copy(_output, bytes, 0, bytes.Length);
            return bytes;
        }

        public void Dispose()
        {
            while (_lent.Count > 0)
            {
                Return(_lent[^1].At);
            }

            Marshal.FreeHGlobal(_output);
            _output = IntPtr.Zero;
        }
    }

    private static RowStream StreamOf(IntPtr opaque) => (RowStream)GCHandle.FromIntPtr(opaque).Target!;

    /// <summary>Lent only if the stream itself cannot be reached, which never happens while an encode runs.</summary>
    private static readonly byte[] NoStreamBlank = GC.AllocateArray<byte>(64 * 1024, pinned: true);

    private static void Fail(IntPtr opaque, Exception ex)
    {
        try
        {
            StreamOf(opaque).Fail(ex);
        }
        catch (Exception)
        {
            // Nothing to record it in; the encode still ends on the refused output.
        }
    }

    private delegate void FormatCallback(IntPtr opaque, ref PixelFormat format);

    private delegate void ExtraFormatCallback(IntPtr opaque, nuint index, ref PixelFormat format);

    private delegate IntPtr DataCallback(IntPtr opaque, nuint x, nuint y, nuint columns, nuint count, out nuint rowBytes);

    private delegate IntPtr ExtraDataCallback(
        IntPtr opaque, nuint index, nuint x, nuint y, nuint columns, nuint count, out nuint rowBytes);

    private delegate void ReleaseBufferCallback(IntPtr opaque, IntPtr buffer);

    private delegate IntPtr GetBufferCallback(IntPtr opaque, ref nuint size);

    private delegate void WrittenCallback(IntPtr opaque, nuint bytes);

    private delegate void SeekCallback(IntPtr opaque, ulong position);

    // Static, so the delegates outlive every encode holding their function pointers. None of them may throw.
    private static readonly FormatCallback ColourFormat = (IntPtr opaque, ref PixelFormat format) =>
    {
        try
        {
            format = new PixelFormat((uint)StreamOf(opaque).Bands, TypeUint8, 0, 0);
        }
        catch (Exception ex)
        {
            Fail(opaque, ex);
        }
    };

    private static readonly DataCallback ColourData =
        (IntPtr opaque, nuint x, nuint y, nuint columns, nuint count, out nuint rowBytes) =>
        {
            try
            {
                return StreamOf(opaque).Lend((int)x, (int)y, (int)count, out rowBytes);
            }
            catch (Exception ex)
            {
                return BlankAfter(opaque, ex, out rowBytes);
            }
        };

    // There are no extra channels, so libjxl never calls these two; they only complete the struct.
    private static readonly ExtraFormatCallback ExtraFormat = (IntPtr opaque, nuint _, ref PixelFormat format) =>
        format = new PixelFormat(1, TypeUint8, 0, 0);

    private static readonly ExtraDataCallback ExtraData =
        (IntPtr opaque, nuint _, nuint x, nuint y, nuint columns, nuint count, out nuint rowBytes) =>
        {
            try
            {
                return StreamOf(opaque).Blank(out rowBytes);
            }
            catch (Exception ex)
            {
                return BlankAfter(opaque, ex, out rowBytes);
            }
        };

    private static IntPtr BlankAfter(IntPtr opaque, Exception ex, out nuint rowBytes)
    {
        Fail(opaque, ex);
        rowBytes = 0;
        return Marshal.UnsafeAddrOfPinnedArrayElement(NoStreamBlank, 0);
    }

    private static readonly ReleaseBufferCallback ReleaseBuffer = (opaque, buffer) =>
    {
        try
        {
            StreamOf(opaque).Return(buffer);
        }
        catch (Exception ex)
        {
            Fail(opaque, ex);
        }
    };

    // A null buffer of size 0 tells libjxl to stop; the encode then fails.
    private static readonly GetBufferCallback GetBuffer = (IntPtr opaque, ref nuint size) =>
    {
        try
        {
            return StreamOf(opaque).OutputBuffer(ref size);
        }
        catch (Exception ex)
        {
            Fail(opaque, ex);
            size = 0;
            return IntPtr.Zero;
        }
    };

    private static readonly WrittenCallback Written = (opaque, bytes) =>
    {
        try
        {
            StreamOf(opaque).Written(bytes);
        }
        catch (Exception ex)
        {
            Fail(opaque, ex);
        }
    };

    private static readonly SeekCallback Seek = (opaque, position) =>
    {
        try
        {
            StreamOf(opaque).Seek(position);
        }
        catch (Exception ex)
        {
            Fail(opaque, ex);
        }
    };

    private static readonly SeekCallback Finalized = (_, _) => { };

    private static readonly IntPtr[] InputCallbacks =
    [
        Marshal.GetFunctionPointerForDelegate(ColourFormat),
        Marshal.GetFunctionPointerForDelegate(ColourData),
        Marshal.GetFunctionPointerForDelegate(ExtraFormat),
        Marshal.GetFunctionPointerForDelegate(ExtraData),
        Marshal.GetFunctionPointerForDelegate(ReleaseBuffer)
    ];

    private static readonly IntPtr[] OutputCallbacks =
    [
        Marshal.GetFunctionPointerForDelegate(GetBuffer),
        Marshal.GetFunctionPointerForDelegate(Written),
        Marshal.GetFunctionPointerForDelegate(Seek),
        Marshal.GetFunctionPointerForDelegate(Finalized)
    ];

    /// <summary>JxlChunkedFrameInputSource: the opaque pointer and five callbacks.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ChunkedSource(IntPtr opaque)
    {
        private readonly IntPtr _opaque = opaque;
        private readonly IntPtr _colourFormat = InputCallbacks[0];
        private readonly IntPtr _colourData = InputCallbacks[1];
        private readonly IntPtr _extraFormat = InputCallbacks[2];
        private readonly IntPtr _extraData = InputCallbacks[3];
        private readonly IntPtr _release = InputCallbacks[4];
    }

    /// <summary>JxlEncoderOutputProcessor: the opaque pointer and four callbacks.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct OutputProcessor(IntPtr opaque)
    {
        private readonly IntPtr _opaque = opaque;
        private readonly IntPtr _getBuffer = OutputCallbacks[0];
        private readonly IntPtr _releaseBuffer = OutputCallbacks[1];
        private readonly IntPtr _seek = OutputCallbacks[2];
        private readonly IntPtr _finalized = OutputCallbacks[3];
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
                        // A second frame would ask again; Maki only decodes the single frames it wrote.
                        if (output.IsAllocated) throw new JpegXlException("more than one frame");
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

    /// <summary>A bare JPEG XL codestream or the ISO container that wraps one.</summary>
    public static bool HasSignature(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(CodestreamSignature) || bytes.StartsWith(ContainerSignature);

    private static ReadOnlySpan<byte> CodestreamSignature => [0xFF, 0x0A];

    private static ReadOnlySpan<byte> ContainerSignature => [0, 0, 0, 0x0C, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A];

    /// <summary>Width and height from the header, without decoding pixels.</summary>
    public static (int Width, int Height) ReadSize(byte[] data)
    {
        var (width, height, _) = ReadHeader(data);
        return (width, height);
    }

    /// <summary>Width, height and colour channels (1 or 3) from the header, without decoding pixels.</summary>
    public static (int Width, int Height, int Bands) ReadHeader(byte[] data)
    {
        EnsureAvailable();
        var decoder = JxlDecoderCreate(IntPtr.Zero);
        if (decoder == IntPtr.Zero) throw new JpegXlException("JxlDecoderCreate");
        var input = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            CheckDec(JxlDecoderSubscribeEvents(decoder, DecBasicInfo), "JxlDecoderSubscribeEvents");
            CheckDec(JxlDecoderSetInput(decoder, input.AddrOfPinnedObject(), (nuint)data.Length), "JxlDecoderSetInput");
            JxlDecoderCloseInput(decoder);
            var status = JxlDecoderProcessInput(decoder);
            if (status != DecBasicInfo) throw new JpegXlException($"JxlDecoderProcessInput returned {status}");
            var info = new int[BasicInfoInts];
            CheckDec(JxlDecoderGetBasicInfo(decoder, info), "JxlDecoderGetBasicInfo");
            return (info[InfoXSize], info[InfoYSize], info[InfoColorChannels] == 1 ? 1 : 3);
        }
        finally
        {
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

    /// <summary>The libjxl file beside the app, whether or not it loads; null when none is there.</summary>
    public static string? LibraryFile =>
        FileNames.Select(file => Path.Combine(AppContext.BaseDirectory, file)).FirstOrDefault(File.Exists);

    /// <summary>
    /// Throws rather than returning zero, which would let the runtime probe the system's library
    /// paths and pick up a distro libjxl Maki was never tested with.
    /// </summary>
    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return IntPtr.Zero;
        if (LibraryFile is { } path && NativeLibrary.TryLoad(path, out var handle)) return handle;
        throw new DllNotFoundException($"libjxl is not beside Maki in {AppContext.BaseDirectory}");
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
    [DllImport(Library)] private static extern int JxlEncoderAddImageFrame(IntPtr settings, ref PixelFormat format, IntPtr buffer, nuint size);
    [DllImport(Library)] private static extern int JxlEncoderAddChunkedFrame(IntPtr settings, int isLastFrame, ChunkedSource source);
    [DllImport(Library)] private static extern int JxlEncoderSetOutputProcessor(IntPtr encoder, OutputProcessor processor);
    [DllImport(Library)] private static extern int JxlEncoderFlushInput(IntPtr encoder);
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

/// <summary>Full-width rows of interleaved 8-bit pixels that <see cref="JpegXl.EncodeRows"/> reads as libjxl asks for them.</summary>
public interface IJpegXlRows
{
    /// <summary>
    /// The address of row <paramref name="top"/>'s first pixel, with the rest of the <paramref name="count"/>
    /// rows <paramref name="stride"/> bytes apart after it. They must stay put until the matching
    /// <see cref="Release"/>. May block until the rows are ready.
    /// </summary>
    IntPtr Acquire(int top, int count, out int stride);

    /// <summary>libjxl is done with the rows of one <see cref="Acquire"/>, which returned <paramref name="rows"/>.</summary>
    void Release(IntPtr rows);
}
