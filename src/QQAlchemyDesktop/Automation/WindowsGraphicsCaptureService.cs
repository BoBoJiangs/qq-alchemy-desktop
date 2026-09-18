using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;

namespace QQAlchemyDesktop.Automation;

/// <summary>Captures one HWND through Windows.Graphics.Capture without reading process memory.</summary>
public sealed class WindowsGraphicsCaptureService : IDisposable
{
    private static readonly Guid GraphicsCaptureItemInterface = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid GraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid DxgiDeviceInterface = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private readonly IDirect3DDevice _device;
    private IntPtr _nativeD3dDevice;
    private IntPtr _nativeD3dContext;

    public WindowsGraphicsCaptureService()
    {
        var hr = NativeCapture.D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero,
            0x20, IntPtr.Zero, 0, 7, out _nativeD3dDevice, out _, out _nativeD3dContext);
        if (hr < 0)
        {
            hr = NativeCapture.D3D11CreateDevice(IntPtr.Zero, 5, IntPtr.Zero,
                0x20, IntPtr.Zero, 0, 7, out _nativeD3dDevice, out _, out _nativeD3dContext);
        }
        Marshal.ThrowExceptionForHR(hr);

        var iid = DxgiDeviceInterface;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_nativeD3dDevice, ref iid, out var dxgiDevice));
        try
        {
            Marshal.ThrowExceptionForHR(NativeCapture.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable));
            try
            {
                _device = MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally
            {
                MarshalInspectable<IDirect3DDevice>.DisposeAbi(inspectable);
            }
        }
        finally
        {
            Marshal.Release(dxgiDevice);
        }
    }

    public async Task<Bitmap> CaptureAsync(IntPtr hwnd, CancellationToken cancellationToken = default)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new InvalidOperationException("当前 Windows 版本不支持 Windows.Graphics.Capture");
        var item = CreateItem(hwnd);
        if (item.Size.Width <= 0 || item.Size.Height <= 0) throw new InvalidOperationException("QQ 捕获区域尺寸无效");
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        session.StartCapture();

        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var frame = pool.TryGetNextFrame();
            if (frame is not null)
            {
                using var softwareBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
                return await ToDrawingBitmapAsync(softwareBitmap);
            }
            await Task.Delay(35, cancellationToken);
        }
        throw new InvalidOperationException("Windows.Graphics.Capture 在超时前未返回 QQ 窗口帧");
    }

    private static GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        using var interop = factory.As(GraphicsCaptureItemInterop);
        var vtable = Marshal.ReadIntPtr(interop.ThisPtr);
        var function = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
        var create = Marshal.GetDelegateForFunctionPointer<CreateForWindowDelegate>(function);
        var iid = GraphicsCaptureItemInterface;
        Marshal.ThrowExceptionForHR(create(interop.ThisPtr, hwnd, ref iid, out var result));
        try
        {
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(result);
        }
        finally
        {
            MarshalInspectable<GraphicsCaptureItem>.DisposeAbi(result);
        }
    }

    private static async Task<Bitmap> ToDrawingBitmapAsync(SoftwareBitmap source)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(source);
        await encoder.FlushAsync();
        stream.Seek(0);
        var bytes = new byte[checked((int)stream.Size)];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)bytes.Length);
            reader.ReadBytes(bytes);
        }
        using var memory = new MemoryStream(bytes, writable: false);
        using var decoded = new Bitmap(memory);
        return decoded.Clone(new Rectangle(0, 0, decoded.Width, decoded.Height), PixelFormat.Format32bppArgb);
    }

    public void Dispose()
    {
        _device?.Dispose();
        if (_nativeD3dContext != IntPtr.Zero) Marshal.Release(_nativeD3dContext);
        if (_nativeD3dDevice != IntPtr.Zero) Marshal.Release(_nativeD3dDevice);
        _nativeD3dContext = IntPtr.Zero;
        _nativeD3dDevice = IntPtr.Zero;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindowDelegate(IntPtr @this, IntPtr hwnd, ref Guid iid, out IntPtr result);

    private static class NativeCapture
    {
        [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device,
            out int featureLevel, out IntPtr immediateContext);

        [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    }
}
