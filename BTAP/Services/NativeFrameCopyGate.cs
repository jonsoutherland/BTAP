namespace BTAP.Services;

/// <summary>
/// Process-wide serializer for <c>MediaPlayer.CopyFrameToVideoSurface</c>. Two
/// FrameServer-enabled MediaPlayers calling it concurrently from worker threads
/// share Media Foundation pipeline state internally and AV the process under
/// load (silent native crash — no managed exception, no DeviceLost). Every
/// caller — the preview compositor AND the export source pool — must hold this
/// lock for the duration of the native copy.
/// </summary>
internal static class NativeFrameCopyGate
{
    public static readonly object Lock = new();
}
