using Microsoft.Graphics.Canvas;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using BTAP.Models;

namespace BTAP.Services;

/// <summary>
/// Media Foundation SinkWriter-based video encoder for export stage 1.
///
/// GPU-direct path: the rendered <see cref="CanvasRenderTarget"/>'s underlying
/// <see cref="ID3D11Texture2D"/> is wrapped in an MF DXGI surface buffer and
/// handed directly to the hardware H.264 encoder via a shared
/// <see cref="IMFDXGIDeviceManager"/>. There's no per-frame
/// <c>GetPixelBytes</c> readback and no CPU-side <c>Marshal.Copy</c> — the
/// pixels stay on the GPU from Win2D's draw through to the encoder's input.
/// The whole pipeline (decoder → composite → encoder) becomes GPU-resident,
/// which is what professional editors do to hit sub-realtime export.
///
/// For this to work Win2D and MF have to share one D3D11 device. We extract
/// the device Win2D is using via <see cref="Win2DInterop.GetD3D11Device"/>,
/// flip on multithread protection (MF requires it), and reset our DXGI device
/// manager onto that device. From there every texture allocated by Win2D is
/// natively addressable by MF.
///
/// Time-unit note: MF uses 100-nanosecond units; so does <see cref="TimeSpan"/>'s
/// Ticks. Sample times map directly.
/// </summary>
public sealed class ExportSinkRenderer : IDisposable
{
    private static int s_mfStartedRefCount;
    private static readonly object s_mfStartLock = new();

    private readonly Project _project;
    private readonly string _outputPath;
    private readonly IMFDXGIDeviceManager _deviceManager;
    private readonly ID3D11Device _d3dDevice;
    private readonly ExportLogger? _log;
    private readonly TimeSpan _frameDuration;
    private readonly int _bitrate;
    private readonly double _frameRate;

    private IMFSinkWriter? _sinkWriter;
    private int _streamIndex;
    private long _sampleTime;

    // Fresh-per-frame texture, deferred disposal of (sample, buffer, texture)
    // tuple for PendingFrameDepth frames. With throttling enabled on the sink
    // writer, MF's internal queue is bounded — so the encoder is reliably
    // done with frame N's texture by the time the deferred queue dequeues it
    // PendingFrameDepth frames later. Fresh allocation per frame (rather than
    // a recycled pool) avoids the risk of overwriting a texture that MF is
    // still reading.
    private const int PendingFrameDepth = 60;
    private readonly Queue<(IMFSample sample, IMFMediaBuffer buf, ID3D11Texture2D tex)> _pendingFrames = new();

    /// <summary>
    /// Constructs the SinkWriter using a shared <paramref name="deviceManager"/>
    /// and the same <paramref name="d3dDevice"/> Win2D and the SourceReader
    /// pool use, so the decoder, compositor, and encoder all sit on one D3D11
    /// device. The device is also used to allocate fresh per-frame textures
    /// in WriteFrame so the encoder always gets a clean ShaderResource-only
    /// texture rather than the compositor's reused RenderTarget RT.
    /// </summary>
    public ExportSinkRenderer(Project project, string outputPath,
                              IMFDXGIDeviceManager deviceManager,
                              ID3D11Device d3dDevice,
                              ExportLogger? log,
                              int? bitrateOverride = null,
                              double? frameRateOverride = null)
    {
        _project       = project;
        _outputPath    = outputPath;
        _deviceManager = deviceManager;
        _d3dDevice     = d3dDevice;
        _log           = log;
        _frameRate     = Math.Max(1, frameRateOverride ?? project.FrameRate);
        _frameDuration = TimeSpan.FromSeconds(1.0 / _frameRate);
        // Was 0.1 (≈12 Mbps for 1080p60); 0.16 lands at ≈20 Mbps which is
        // closer to what other editors default to for "high quality" 1080p.
        _bitrate       = bitrateOverride
                         ?? (int)Math.Max(2_000_000,
                                          project.Width * project.Height * _frameRate * 0.16);
    }

    private static void EnsureMFStarted()
    {
        lock (s_mfStartLock)
        {
            if (s_mfStartedRefCount++ == 0)
                MediaFactory.MFStartup(false); // full (non-lite) startup
        }
    }

    private static void ShutdownMFIfLast()
    {
        lock (s_mfStartLock)
        {
            if (--s_mfStartedRefCount == 0)
                MediaFactory.MFShutdown();
        }
    }

    // FrameSize, FrameRate, PixelAspectRatio are stored as packed 64-bit MF
    // attributes: high 32 = width / numerator, low 32 = height / denominator.
    private static ulong PackUint32Pair(uint high, uint low)
        => ((ulong)high << 32) | low;

    public void Begin()
    {
        EnsureMFStarted();

        // Sink writer attributes — hardware transforms enabled, throttling off,
        // and the externally-provided DXGI manager so the encoder reads GPU
        // surfaces directly.
        // Throttling on (DisableThrottling NOT set): MF natively back-pressures
        // WriteSample so its internal queue stays bounded.
        // ReadwriteEnableHardwareTransforms = 0 forces the SOFTWARE H.264
        // encoder MFT. Hardware encoder MFT proved fundamentally
        // unreliable at 60 fps + two source layers — composite repeatedly
        // collapsed to black around frame 600. Software encoder is slower
        // but produces deterministic output.
        IMFAttributes attrs = MediaFactory.MFCreateAttributes(3u);
        attrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 0u);
        attrs.Set(SinkWriterAttributeKeys.D3DManager,                        _deviceManager);

        _sinkWriter = MediaFactory.MFCreateSinkWriterFromURL(_outputPath, null, attrs);
        attrs.Dispose();

        // Output (compressed) media type: H.264 at project resolution + framerate.
        IMFMediaType outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType,        MediaTypeGuids.Video);
        outputType.Set(MediaTypeAttributeKeys.Subtype,          VideoFormatGuids.H264);
        outputType.Set(MediaTypeAttributeKeys.AvgBitrate,       (uint)_bitrate);
        outputType.Set(MediaTypeAttributeKeys.InterlaceMode,    (uint)VideoInterlaceMode.Progressive);
        outputType.Set(MediaTypeAttributeKeys.FrameSize,        PackUint32Pair((uint)_project.Width, (uint)_project.Height));
        outputType.Set(MediaTypeAttributeKeys.FrameRate,        PackUint32Pair((uint)Math.Round(_frameRate), 1));
        outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackUint32Pair(1, 1));

        // H.264 Profile + Level: critical at 60 fps. Without an explicit
        // MF_MT_MPEG2_LEVEL the Microsoft hardware H.264 encoder MFT defaults
        // to Level 4.0, which spec-caps at 1920×1080 @ 30 fps. Feeding it
        // 1080p60 (which 1080×1920 portrait is from the bit-budget side) is
        // out-of-spec — the encoder emits a valid first GOP and then degrades
        // to all-skip P-frames for the remainder, which is exactly the
        // "first second visible, then black" symptom we reproduced at 60 fps
        // but not at 30 fps. Level 4.2 is the spec level for 1080p60.
        // High Profile is standard for 1080p delivery.
        outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, 100u); // eAVEncH264VProfile_High
        outputType.Set(MediaTypeAttributeKeys.Mpeg2Level,   42u);  // eAVEncH264VLevel4_2

        _streamIndex = _sinkWriter.AddStream(outputType);
        outputType.Dispose();

        // Input (uncompressed) media type: BGRA32 — matches Win2D's render
        // target pixel layout. MF's hardware Color Converter MFT handles
        // BGRA→NV12 conversion entirely on the GPU.
        //
        // DefaultStride is CRITICAL when the input is a DXGI surface buffer.
        // Without it, the encoder MFT picks a default stride (often a power-of-2
        // alignment like 4096) which doesn't match the natural row pitch of our
        // texture (width × 4 bytes). The mismatch reads each scanline at the
        // wrong byte offset, which manifests in the encoded output as a
        // diagonal shear / trapezoid (because consecutive rows are offset by
        // a constant amount of pixels). This was the visual bug we kept
        // chasing through the source pool / staging RT / output ring changes —
        // it was always the encoder reading the input with the wrong pitch.
        IMFMediaType inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType,        MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype,          VideoFormatGuids.Argb32);
        inputType.Set(MediaTypeAttributeKeys.InterlaceMode,    (uint)VideoInterlaceMode.Progressive);
        inputType.Set(MediaTypeAttributeKeys.FrameSize,        PackUint32Pair((uint)_project.Width, (uint)_project.Height));
        inputType.Set(MediaTypeAttributeKeys.FrameRate,        PackUint32Pair((uint)Math.Round(_frameRate), 1));
        inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackUint32Pair(1, 1));
        inputType.Set(MediaTypeAttributeKeys.DefaultStride,    (uint)(_project.Width * 4));

        _sinkWriter.SetInputMediaType(_streamIndex, inputType, null);
        inputType.Dispose();

        _sinkWriter.BeginWriting();
        _sampleTime = 0;

        _log?.Log($"   [sink] writer started — H.264 {_project.Width}x{_project.Height} @ {_frameRate}fps, " +
                  $"bitrate={_bitrate}, GPU-direct (no CPU readback), retention={PendingFrameDepth}");
    }

    /// <summary>Encodes one composited frame. We allocate a fresh D3D11
    /// texture per call, CopyResource the compositor's output into it, and
    /// hand THAT texture to the encoder. Reasons:
    ///  • The encoder needs an input texture with ShaderResource bind flag
    ///    and a clean resource state; the compositor's RT is multi-bind
    ///    (RenderTarget | ShaderResource) and has just finished a Win2D draw.
    ///  • The encoder retains internal references to queued input samples
    ///    for B-frame analysis; if we handed it the same compositor RT every
    ///    frame, the next frame's draw would overwrite the data the encoder
    ///    still has queued (the original "first frame OK, rest black/scrambled"
    ///    bug). Per-frame texture handoff gives each queued sample its own
    ///    stable GPU memory until the encoder is done with it.
    /// The fresh texture's lifetime is owned by the IMFSample we pass to
    /// WriteSample; MF Releases it when the encoder finishes the frame.</summary>
    public void WriteFrame(CanvasRenderTarget frame)
    {
        if (_sinkWriter is null) throw new InvalidOperationException("Begin() not called");

        var desc = new Texture2DDescription
        {
            Width             = (uint)_project.Width,
            Height            = (uint)_project.Height,
            MipLevels         = 1,
            ArraySize         = 1,
            Format            = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage             = ResourceUsage.Default,
            BindFlags         = BindFlags.ShaderResource,
            CPUAccessFlags    = CpuAccessFlags.None,
            MiscFlags         = ResourceOptionFlags.None,
        };
        var sampleTexture = _d3dDevice.CreateTexture2D(ref desc);

        // GPU→GPU copy: compositor's output into the fresh texture.
        using (var compositorTexture = Win2DInterop.GetD3D11Texture2D(frame))
        {
            _d3dDevice.ImmediateContext.CopyResource(sampleTexture, compositorTexture);
        }

        // (Removed per-frame Flush(): with multithread protection enabled on
        // the device, D3D11 already synchronises CopyResource against MF's
        // subsequent read on its own context. Empirically the explicit
        // Flush was harmless at 30 fps but at 60 fps appears to interact
        // badly with Win2D's per-frame draws on the same immediate context —
        // the compositor's next-frame DrawImage silently produces black
        // even though the inputs are correct. Letting D3D11 manage its own
        // command-queue ordering restores 60 fps behaviour without
        // regressing 30 fps.)

        // Diagnostic: dump the encoder's input texture for the first few
        // frames so we can tell whether the GPU→GPU copy preserved content
        // all the way to the H.264 input. Plus periodic checkpoint dumps
        // (at frames 60, 600, 1200, 1800, 2400) so we can confirm the
        // pipeline keeps feeding fresh content all the way through —
        // i.e. distinguish "encoder is fed correct frames but produces
        // black after frame N" from "pipeline goes stale after frame N".
        int diagIdx = ExportDiag.NextIndex("encoder-input", 3);
        if (diagIdx >= 0)
            ExportDiag.DumpTexture(frame.Device, sampleTexture, $"3-encoder-{diagIdx}.png", _log);
        else if (ExportDiag.ShouldDumpAtCheckpoint((int)(_sampleTime / _frameDuration.Ticks)))
        {
            int chkIdx = (int)(_sampleTime / _frameDuration.Ticks);
            ExportDiag.DumpTexture(frame.Device, sampleTexture, $"3-encoder-checkpoint-{chkIdx}.png", _log);
        }

        // Wrap the fresh texture as an MF DXGI surface buffer.
        IMFMediaBuffer dxgiBuf = MediaFactory.MFCreateDXGISurfaceBuffer(
            Win2DInterop.ID3D11Texture2DGuid, sampleTexture, 0u, false);

        // Set the contiguous length. For BGRA32 this is just w*h*4.
        dxgiBuf.CurrentLength = _project.Width * _project.Height * 4;

        IMFSample sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(dxgiBuf);
        sample.SampleTime     = _sampleTime;
        sample.SampleDuration = _frameDuration.Ticks;

        _sinkWriter.WriteSample(_streamIndex, sample);

        _pendingFrames.Enqueue((sample, dxgiBuf, sampleTexture));
        while (_pendingFrames.Count > PendingFrameDepth)
        {
            var stale = _pendingFrames.Dequeue();
            try { stale.sample.Dispose(); } catch { }
            try { stale.buf.Dispose();    } catch { }
            try { stale.tex.Dispose();    } catch { }
        }

        _sampleTime += _frameDuration.Ticks;
    }

    public void Finish()
    {
        if (_sinkWriter is null) return;
        _sinkWriter.Finalize();
        _log?.Log("   [sink] writer finalized");
        DrainPendingFrames();
    }

    private void DrainPendingFrames()
    {
        while (_pendingFrames.Count > 0)
        {
            var f = _pendingFrames.Dequeue();
            try { f.sample.Dispose(); } catch { }
            try { f.buf.Dispose();    } catch { }
            try { f.tex.Dispose();    } catch { }
        }
    }

    public void Dispose()
    {
        DrainPendingFrames();
        try { _sinkWriter?.Dispose(); } catch { }
        _sinkWriter = null;
        // _deviceManager is owned by the caller — don't dispose here.
        ShutdownMFIfLast();
    }
}
