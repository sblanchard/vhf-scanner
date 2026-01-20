namespace VhfScanner.Core.Asr;

/// <summary>
/// Interface for speech-to-text transcription services
/// </summary>
public interface IAsrService : IAsyncDisposable
{
    /// <summary>
    /// Transcribe audio samples to text
    /// </summary>
    /// <param name="samples">Audio samples as 16kHz mono float32</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Transcription result with text and metadata</returns>
    Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> samples, CancellationToken ct = default);
    
    /// <summary>
    /// Transcribe audio from a WAV file
    /// </summary>
    Task<TranscriptionResult> TranscribeFileAsync(string filePath, CancellationToken ct = default);
    
    /// <summary>
    /// Whether the service is ready for transcription
    /// </summary>
    bool IsReady { get; }
}

public sealed record TranscriptionResult
{
    public required string Text { get; init; }
    public double Confidence { get; init; } = 1.0;
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<WordTimestamp> Words { get; init; } = [];
    public string? DetectedLanguage { get; init; }
}

public sealed record WordTimestamp
{
    public required string Word { get; init; }
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public double Confidence { get; init; } = 1.0;
}
