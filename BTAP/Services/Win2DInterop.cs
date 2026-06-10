using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace BTAP.Services;

/// <summary>
/// Bridges Win2D's WinRT-projected types (<see cref="CanvasDevice"/>,
/// <see cref="CanvasBitmap"/>) with raw D3D11 COM resources so they can be
/// handed to / received from Media Foundation, which only speaks classic COM,
/// not WinRT.
///
/// Two directions:
///   * Outbound (CanvasDevice / CanvasBitmap → ID3D11Device / ID3D11Texture2D):
///     used by the SinkWriter to hand the encoder Win2D's rendered output
///     surface and to share the D3D11 device with MF via IMFDXGIDeviceManager.
///   * Inbound (ID3D11Texture2D → CanvasBitmap): used by the SourceReader pool
///     to wrap a decoder's GPU output texture so the existing Win2D-based
///     compositor can DrawImage from it without a CPU round-trip.
/// </summary>
internal static class Win2DInterop
{
    // Standard D3D11 interface IDs.
    private static readonly Guid IID_ID3D11Device    = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface([In] ref Guid iid, out IntPtr ppv);
    }

    // P/Invoke for the WinRT helper that wraps a DXGI surface as a WinRT
    // IDirect3DSurface (which is what Win2D's CanvasBitmap.CreateFromDirect3D11Surface
    // accepts). Exported from d3d11.dll on Windows 8.1+.
    [DllImport("d3d11.dll", ExactSpelling = true)]
    [PreserveSig]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);

    public static ID3D11Device GetD3D11Device(CanvasDevice canvasDevice)
    {
        var access = AsDxgiInterfaceAccess(canvasDevice);
        try
        {
            var iid = IID_ID3D11Device;
            int hr = access.GetInterface(ref iid, out IntPtr ptr);
            if (hr < 0) throw new COMException("CanvasDevice → ID3D11Device QueryInterface failed", hr);
            return new ID3D11Device(ptr);
        }
        finally
        {
            Marshal.ReleaseComObject(access);
        }
    }

    public static ID3D11Texture2D GetD3D11Texture2D(CanvasBitmap bitmap)
    {
        var access = AsDxgiInterfaceAccess(bitmap);
        try
        {
            var iid = IID_ID3D11Texture2D;
            int hr = access.GetInterface(ref iid, out IntPtr ptr);
            if (hr < 0) throw new COMException("CanvasBitmap → ID3D11Texture2D QueryInterface failed", hr);
            return new ID3D11Texture2D(ptr);
        }
        finally
        {
            Marshal.ReleaseComObject(access);
        }
    }

    /// <summary>
    /// Wraps an <see cref="ID3D11Texture2D"/> as a <see cref="CanvasBitmap"/> tied
    /// to <paramref name="device"/>. The bitmap shares the texture's GPU memory —
    /// no copy. Caller is responsible for keeping the texture alive at least as
    /// long as the bitmap. Used by the SourceReader pool to surface decoder
    /// output textures to the Win2D compositor.
    ///
    /// Alpha mode is forced to <see cref="Microsoft.Graphics.Canvas.CanvasAlphaMode.Ignore"/>
    /// because MF's ARGB32 color converter doesn't reliably set the alpha byte
    /// to 0xFF for sources without an alpha channel (and video sources don't
    /// have one). With the default premultiplied mode, an alpha of 0 makes
    /// Win2D read the entire pixel as transparent black — DrawImage then
    /// blends nothing onto the output, the encoder sees identical near-black
    /// frames, and the export collapses into a ~200 Kbps mostly-black H.264
    /// stream. Ignore mode tells Win2D to sample as if alpha = 1 everywhere.
    /// </summary>
    public static CanvasBitmap WrapAsCanvasBitmap(CanvasDevice device, ID3D11Texture2D texture)
    {
        // ID3D11Texture2D implements IDXGISurface. Grab that view of it.
        var dxgi = texture.QueryInterface<IDXGISurface>();
        try
        {
            // Promote the DXGI surface to a WinRT IDirect3DSurface via the
            // d3d11.dll helper.
            int hr = CreateDirect3D11SurfaceFromDXGISurface(dxgi.NativePointer, out IntPtr graphicsSurfacePtr);
            if (hr < 0) throw new COMException("CreateDirect3D11SurfaceFromDXGISurface failed", hr);
            try
            {
                var direct3dSurface = MarshalInspectable<IDirect3DSurface>.FromAbi(graphicsSurfacePtr);
                return CanvasBitmap.CreateFromDirect3D11Surface(
                    device, direct3dSurface, 96.0f,
                    Microsoft.Graphics.Canvas.CanvasAlphaMode.Ignore);
                // DO NOT call Dispose() on direct3dSurface here. The CsWinRT
                // projected IDirect3DSurface IS IDisposable in this version,
                // and disposing it releases the underlying COM ref before
                // CanvasBitmap is done with the surface — verified to break
                // the 720p source's staging RT in headless tests. The
                // wrapper's COM ref will be released via finalizer when GC
                // runs, which is fine for export lifetimes.
            }
            finally
            {
                Marshal.Release(graphicsSurfacePtr);
            }
        }
        finally
        {
            dxgi.Dispose();
        }
    }

    public static Guid ID3D11Texture2DGuid => IID_ID3D11Texture2D;

    /// <summary>
    /// CsWinRT-projected objects can't be type-cast directly to a [ComImport]
    /// interface (managed cast throws InvalidCastException). We have to drop to
    /// IUnknown via Marshal.GetIUnknownForObject and explicitly QueryInterface,
    /// then re-wrap the resulting interface pointer.
    /// </summary>
    private static IDirect3DDxgiInterfaceAccess AsDxgiInterfaceAccess(object winrtObject)
    {
        IntPtr unk = Marshal.GetIUnknownForObject(winrtObject);
        try
        {
            Guid iid = typeof(IDirect3DDxgiInterfaceAccess).GUID;
            int hr = Marshal.QueryInterface(unk, ref iid, out IntPtr accessPtr);
            if (hr < 0)
                throw new COMException("QueryInterface(IDirect3DDxgiInterfaceAccess) failed", hr);
            try
            {
                return (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
            }
            finally
            {
                Marshal.Release(accessPtr);
            }
        }
        finally
        {
            Marshal.Release(unk);
        }
    }
}
