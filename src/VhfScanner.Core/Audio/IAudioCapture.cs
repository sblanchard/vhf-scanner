using System.Threading.Channels;

namespace VhfScanner.Core.Audio;

/// <summary>
/// Platform-agnostic interface for audio capture
/// </summary>
public interface IAudioCapture : IAsyncDisposable
{
    ChannelReader<AudioChunk> AudioStream { get; }
    bool IsCapturing { get; }
    void StartCapture();
    void StopCapture();
}
