using System.Collections.Concurrent;
using Microsoft.Graphics.Canvas;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace BTAP.Services;

/// <summary>
/// IMFSourceReader-based source pool — replaces the MediaPlayer pool for export
/// stage 1.
///
/// Why the change: MediaPlayer is a *playback* component; it has realtime
/// semantics, buffered look-ahead, and stop/start latency that all fight against
/// non-realtime batch decoding. Single-stepping it via Play/Pause was capping us
/// at roughly 1× realtime per source. SourceReader is the canonical MF batch
/// decoder — purpose-built for non-realtime workflows. It decodes on demand at
/// hardware speed with no playback pacing, and with the DXGI device manager
/// attached it produces decoded frames as IMFSample / IMFDXGIBuffer / D3D11
/// textures that the rest of the export pipeline can consume on GPU.
///
/// Per-frame flow:
///   1. ReadSample on the reader's video stream. SourceReader hands back the
///      next decoded sample (or seeks forward if we just called SetCurrentPosition).
///   2. Pull the underlying ID3D11Texture2D out of the sample's IMFDXGIBuffer.
///   3. CopySubresourceRegion that texture (which is one slice of the decoder's
///      texture array) into a per-source staging texture.
///   4. The staging texture is wrapped *once* (at PrepareAsync) as a CanvasBitmap;
///      the compositor reads it on every frame.
///
/// Same frame-rate-aware sampling rule as before: if the cached frame still
/// covers the requested output time (within one measured source-frame interval),
/// we skip the ReadSample entirely.
/// </summary>
public sealed class ExportSourceReaderPool : IDisposable
{
    // Per MSDN: MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC,
    // MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE. Vortice's SourceReaderIndex
    // enum carries these as named members but the integer values we have to
    // pass through ReadSample(int, ...) are these magic constants.
    private const int MF_SOURCE_READER_FIRST_VIDEO_STREAM = unchecked((int)0xFFFFFFFC);
    private const int MF_SOURCE_READER_ALL_STREAMS        = unchecked((int)0xFFFFFFFE);

    private sealed class Source : IDisposable
    {
        public string Path = "";
        public IMFSourceReader Reader = null!;
        public int VideoStreamIndex;
        public uint Width;
        public uint Height;

        // Single staging CanvasRenderTarget per source. Win2D writes into it
        // via its own drawing session (in CopySampleToStaging below), which
        // is what makes the compositor's subsequent DrawImage actually see
        // the new content. External CopySubresourceRegion doesn't go through
        // Win2D / D2D's modification tracking, so D2D's bitmap cache for the
        // RT stays stale and the compositor reads old data — that was the
        // "frame 0 OK, rest black" bug. Routing the copy through Win2D fixes it.
        public CanvasRenderTarget? Staging;

        public readonly SemaphoreSlim Gate = new(1, 1);

        public TimeSpan LastDeliveredPosition = TimeSpan.FromTicks(-1);
        public TimeSpan SourceFrameInterval;
        public bool SourceIntervalMeasured;
        public bool EndOfStream;

        // Diagnostic: log the first few sample copies so we can see what the
        // decoder is actually handing us (format, dimensions, subresource).
        public int DiagLogged;
        public const int MaxDiagLogs = 3;

        public void Dispose()
        {
            try { Staging?.Dispose(); } catch { }
            try { Reader?.Dispose();  } catch { }
            try { Gate.Dispose();     } catch { }
        }
    }

    private readonly CanvasDevice _canvasDevice;
    private readonly ID3D11Device _d3dDevice;
    private readonly IMFDXGIDeviceManager _deviceManager;
    private readonly ConcurrentDictionary<string, Source> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ExportLogger? _log;

    public ExportSourceReaderPool(
        CanvasDevice canvasDevice,
        ID3D11Device d3dDevice,
        IMFDXGIDeviceManager deviceManager,
        ExportLogger? log = null)
    {
        _canvasDevice  = canvasDevice;
        _d3dDevice     = d3dDevice;
        _deviceManager = deviceManager;
        _log           = log;
    }

    /// <summary>Pre-opens a source file: creates the reader, picks the first
    /// video stream, configures it to deliver BGRA32 D3D11 surfaces, and
    /// allocates the per-source staging texture + CanvasBitmap wrapper.</summary>
    public Task PrepareAsync(string filePath)
    {
        if (_sources.ContainsKey(filePath)) return Task.CompletedTask;

        IMFSourceReader? reader = null;
        try
        {
            // D3DManager + EnableAdvancedVideoProcessing tell the reader to keep
            // frames on the GPU and use hardware MFTs (DXVA decoder + Color
            // Converter) for any format conversion the encoder needs.
            IMFAttributes attrs = MediaFactory.MFCreateAttributes(4u);
            attrs.Set(SourceReaderAttributeKeys.D3DManager,                   _deviceManager);
            attrs.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1u);
            attrs.Set(SourceReaderAttributeKeys.DisableDxva,                   0u);

            reader = MediaFactory.MFCreateSourceReaderFromURL(filePath, attrs);
            attrs.Dispose();

            int videoIdx = MF_SOURCE_READER_FIRST_VIDEO_STREAM;
            reader.SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, false);
            reader.SetStreamSelection(videoIdx,                     true);

            // Output: BGRA32 progressive. SourceReader will insert a hardware
            // color-conversion MFT between the decoder (which produces NV12)
            // and us, all on GPU.
            IMFMediaType outputType = MediaFactory.MFCreateMediaType();
            outputType.Set(MediaTypeAttributeKeys.MajorType,     MediaTypeGuids.Video);
            outputType.Set(MediaTypeAttributeKeys.Subtype,       VideoFormatGuids.Argb32);
            outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            reader.SetCurrentMediaType(videoIdx, outputType);
            outputType.Dispose();

            // Inspect the actual configured type to learn the source's frame
            // dimensions (which may have been adjusted by the reader to match
            // the decoder's native size), and confirm it actually accepted
            // BGRA32 — if the reader silently fell back to NV12 here our
            // CopySubresourceRegion below would fail format-compatibility
            // checks and produce empty staging textures.
            IMFMediaType current = reader.GetCurrentMediaType(videoIdx);
            ulong frameSize = current.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            Guid actualSubtype = current.GetGUID(MediaTypeAttributeKeys.Subtype);
            current.Dispose();
            uint w = (uint)(frameSize >> 32);
            uint h = (uint)(frameSize & 0xFFFFFFFF);
            if (actualSubtype != VideoFormatGuids.Argb32)
                _log?.Log($"   [reader] WARNING {System.IO.Path.GetFileName(filePath)}: " +
                          $"asked for ARGB32 but got subtype {actualSubtype}");

            // Single staging CanvasRenderTarget per source. AlphaMode=Ignore
            // because MF's color converter doesn't reliably set the alpha
            // byte for sources without an alpha channel.
            var staging = new CanvasRenderTarget(
                _canvasDevice,
                (float)w, (float)h, 96.0f,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                Microsoft.Graphics.Canvas.CanvasAlphaMode.Ignore);

            var src = new Source
            {
                Path             = filePath,
                Reader           = reader,
                VideoStreamIndex = videoIdx,
                Width            = w,
                Height           = h,
                Staging          = staging,
            };
            if (!_sources.TryAdd(filePath, src))
            {
                src.Dispose();
                return Task.CompletedTask;
            }
            reader = null; // ownership transferred
            _log?.Log($"   [reader] opened {System.IO.Path.GetFileName(filePath)} ({w}x{h})");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _log?.Log($"   [reader] FAILED to open {filePath}: {ex.GetType().Name}: {ex.Message} HRESULT=0x{ex.HResult:X8}");
            try { reader?.Dispose(); } catch { }
            throw;
        }
    }

    public async Task<CanvasBitmap?> GetFrameAsync(string filePath, TimeSpan sourceTime, CancellationToken ct = default)
    {
        if (!_sources.TryGetValue(filePath, out var src)) return null;

        await src.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Cached frame reuse — forward-only window: reuse cached staging
            // if the requested output time is within one source-frame interval
            // ahead of the cached sample's timestamp. Produces a slightly
            // irregular cadence on rate conversion (e.g. 30fps→60fps gives
            // a 1-3-3 pattern rather than the ideal 2-2-2), but it's what the
            // pipeline reliably worked with at 2.9 Mbps. Trying to be cleverer
            // (nearest-neighbor with a half-interval window) regressed it back
            // to the 200-600Kbps black-output state — likely because the
            // additional WrapAsCanvasBitmap calls per second pushed cumulative
            // CsWinRT RCW leakage past a threshold.
            if (src.SourceIntervalMeasured
                && src.LastDeliveredPosition >= TimeSpan.Zero
                && sourceTime >= src.LastDeliveredPosition
                && sourceTime < src.LastDeliveredPosition + src.SourceFrameInterval)
            {
                return src.Staging;
            }

            // Decide seek vs. continue. SourceReader's SetCurrentPosition seeks
            // forward to the next keyframe ≤ the target; then we read forward
            // until ReadSample's timestamp catches up.
            var diff = sourceTime - src.LastDeliveredPosition;
            bool needSeek = src.LastDeliveredPosition < TimeSpan.Zero
                         || diff < TimeSpan.FromMilliseconds(-50)
                         || diff > TimeSpan.FromSeconds(2);

            if (needSeek)
            {
                try { src.Reader.SetCurrentPosition(sourceTime.Ticks); } catch { }
                src.LastDeliveredPosition  = TimeSpan.FromTicks(-1);
                src.SourceIntervalMeasured = false;
                src.EndOfStream            = false;
            }

            if (src.EndOfStream) return src.Staging;

            // Drain samples until we get one at or past sourceTime, or hit EOS.
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                IMFSample? sample;
                int      actualStreamIndex;
                SourceReaderFlag flags;
                long     timestamp100ns;
                try
                {
                    sample = src.Reader.ReadSample(
                        src.VideoStreamIndex, SourceReaderControlFlag.None,
                        out actualStreamIndex, out flags, out timestamp100ns);
                }
                catch (Exception ex)
                {
                    _log?.Log($"   [reader] ReadSample THREW for {System.IO.Path.GetFileName(filePath)}: " +
                              $"{ex.GetType().Name}: {ex.Message} HRESULT=0x{ex.HResult:X8}");
                    return src.Staging;
                }

                if ((flags & SourceReaderFlag.EndOfStream) != 0)
                {
                    src.EndOfStream = true;
                    try { sample?.Dispose(); } catch { }
                    return src.Staging;
                }
                if (sample is null)
                {
                    // No sample this iteration (could be a gap / format change);
                    // keep polling.
                    continue;
                }

                var samplePos = TimeSpan.FromTicks(timestamp100ns);

                // Update the minimum-observed source-frame interval if this
                // delta is smaller than what we've seen before. Acts as a
                // robust floor in case of irregular sample timing.
                if (src.LastDeliveredPosition >= TimeSpan.Zero)
                {
                    var delta = samplePos - src.LastDeliveredPosition;
                    if (delta > TimeSpan.FromMilliseconds(5) && delta < TimeSpan.FromMilliseconds(200))
                    {
                        if (!src.SourceIntervalMeasured || delta < src.SourceFrameInterval)
                        {
                            src.SourceFrameInterval    = delta;
                            src.SourceIntervalMeasured = true;
                        }
                    }
                }

                // If this sample's timestamp is still before the target, drop
                // it and keep reading. (Common right after a seek — the reader
                // hands back the keyframe first, then walks forward.)
                if (samplePos < sourceTime - TimeSpan.FromMilliseconds(0.5))
                {
                    src.LastDeliveredPosition = samplePos; // still useful for interval measurement
                    sample.Dispose();
                    continue;
                }

                // This sample is the one. Copy its texture into our staging
                // CanvasRenderTarget so the compositor can read from a stable
                // Win2D-owned surface.
                try
                {
                    CopySampleToStaging(src, sample);
                }
                catch (Exception ex)
                {
                    _log?.Log($"   [reader] copy-to-staging THREW for {System.IO.Path.GetFileName(filePath)} @ {samplePos}: " +
                              $"{ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    sample.Dispose();
                }

                src.LastDeliveredPosition = samplePos;
                return src.Staging;
            }
        }
        finally
        {
            src.Gate.Release();
        }
    }

    private void CopySampleToStaging(Source src, IMFSample sample)
    {
        // Each MF sample carries one or more IMFMediaBuffers; on D3D11 the
        // first buffer wraps the decoder's output texture array.
        IMFMediaBuffer buffer;
        try { buffer = sample.ConvertToContiguousBuffer(); }
        catch (Exception)
        {
            // Some samples already have a single contiguous buffer; fall back.
            buffer = sample.GetBufferByIndex(0);
        }

        try
        {
            // Promote to IMFDXGIBuffer to access the underlying D3D11 resource.
            // QueryInterface failure here means the sample landed in CPU memory
            // (no D3D11 path) — the SourceReader's D3DManager attribute wasn't
            // honored. That's a hard failure for our pipeline; log it loudly.
            IMFDXGIBuffer dxgiBuf;
            try
            {
                dxgiBuf = buffer.QueryInterface<IMFDXGIBuffer>();
            }
            catch (Exception ex)
            {
                _log?.Log($"   [reader-diag] {System.IO.Path.GetFileName(src.Path)}: " +
                          $"buffer is NOT IMFDXGIBuffer — D3DManager bypassed? " +
                          $"{ex.GetType().Name}: {ex.Message}");
                return;
            }

            try
            {
                var textureIid = Win2DInterop.ID3D11Texture2DGuid;
                IntPtr texturePtr = dxgiBuf.GetResource(textureIid);
                using var srcTexture = new ID3D11Texture2D(texturePtr);

                if (src.DiagLogged < Source.MaxDiagLogs)
                {
                    var srcDesc = srcTexture.Description;
                    _log?.Log($"   [reader-diag] {System.IO.Path.GetFileName(src.Path)} #{src.DiagLogged}: " +
                              $"srcTex={srcDesc.Width}x{srcDesc.Height} fmt={srcDesc.Format} " +
                              $"arr={srcDesc.ArraySize} bind={srcDesc.BindFlags}");
                    src.DiagLogged++;
                }

                // Wrap the decoder's output texture as a temporary CanvasBitmap
                // and use Win2D's drawing session to copy it into the staging RT.
                //
                // Why go through Win2D instead of D3D11's CopySubresourceRegion?
                // Because the compositor's later DrawImage from the staging RT
                // only sees content that Win2D itself wrote into it. D2D's
                // bitmap cache for the RT isn't invalidated by external D3D11
                // modifications — that's the entire reason every export so far
                // produced an all-black video after the first frame. By making
                // Win2D the one that writes into the RT, the modification goes
                // through D2D's own pipeline and the cache stays consistent.
                using var srcBitmap = Win2DInterop.WrapAsCanvasBitmap(_canvasDevice, srcTexture);
                using (var ds = src.Staging!.CreateDrawingSession())
                {
                    ds.DrawImage(srcBitmap);
                }

                // Diagnostic: dump the staging RT for the first few copies of
                // each source so we can see whether decoded content is actually
                // landing in the Win2D-owned staging surface. Plus a checkpoint
                // dump whenever the orchestrator is on a checkpoint output
                // frame, so we can tell whether the staging surfaces stay
                // populated deep into the timeline (or go black at the same
                // point the encoder input does).
                int diagIdx = ExportDiag.NextIndex("staging:" + src.Path, 2);
                var safeName = System.IO.Path.GetFileNameWithoutExtension(src.Path);
                if (diagIdx >= 0)
                {
                    ExportDiag.DumpRT(src.Staging!, $"1-staging-{safeName}-{diagIdx}.png", _log);
                }
                else if (ExportDiag.ShouldDumpAtCheckpoint(ExportDiag.CurrentFrame))
                {
                    ExportDiag.DumpRT(src.Staging!, $"1-staging-{safeName}-checkpoint-{ExportDiag.CurrentFrame}.png", _log);
                }
            }
            finally
            {
                dxgiBuf.Dispose();
            }
        }
        finally
        {
            buffer.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var src in _sources.Values) src.Dispose();
        _sources.Clear();
    }
}
