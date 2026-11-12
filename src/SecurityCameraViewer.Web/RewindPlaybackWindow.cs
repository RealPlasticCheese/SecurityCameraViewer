namespace SecurityCameraViewer.Web;

/// <summary>Immutable media references admitted under the rewind buffer lock.
/// Eviction or startup timing refinement cannot invalidate an in-flight load.</summary>
public sealed record RewindPlaybackWindow(RewindPlaylist Playlist, byte[] Initialization, byte[][] Fragments);

public sealed record RewindWindowHeader(RewindPlaylist Playlist, int InitializationLength, int[] FragmentLengths);
