using Microsoft.Extensions.Logging;

namespace VhfScanner.Core.Audio;

/// <summary>
/// Records transmissions when squelch is open, with pre-roll buffer
/// </summary>
public sealed class TransmissionRecorder
{
    private readonly ILogger<TransmissionRecorder> _logger;
    private readonly int _sampleRate;
    private readonly int _preRollSamples;
    private readonly int _maxDurationSamples;
    private readonly int _minDurationSamples;
    private readonly int _silenceTailSamples;
    
    private readonly Queue<float> _preRollBuffer;
    private readonly List<float> _recordingBuffer;
    private bool _isRecording;
    private int _silenceCounter;
    
    public bool IsRecording => _isRecording;

    public TransmissionRecorder(
        ILogger<TransmissionRecorder> logger,
        int sampleRate = 48000,
        double preRollSeconds = 0.5,
        double maxDurationSeconds = 60,
        double minDurationSeconds = 1.0,
        double silenceTailSeconds = 1.0)
    {
        _logger = logger;
        _sampleRate = sampleRate;
        _preRollSamples = (int)(sampleRate * preRollSeconds);
        _maxDurationSamples = (int)(sampleRate * maxDurationSeconds);
        _minDurationSamples = (int)(sampleRate * minDurationSeconds);
        _silenceTailSamples = (int)(sampleRate * silenceTailSeconds);
        
        _preRollBuffer = new Queue<float>(_preRollSamples);
        _recordingBuffer = new List<float>(_sampleRate * 10); // Pre-allocate for 10 seconds
    }

    /// <summary>
    /// Process audio samples with current squelch state
    /// </summary>
    /// <returns>Completed recording if transmission ended, null otherwise</returns>
    public RecordedTransmission? ProcessSamples(ReadOnlySpan<float> samples, bool squelchOpen)
    {
        if (squelchOpen)
        {
            if (!_isRecording)
            {
                StartRecording();
            }
            
            // Add samples to recording
            AddToRecording(samples);
            _silenceCounter = 0;
        }
        else
        {
            if (_isRecording)
            {
                // Continue recording during silence tail
                _silenceCounter += samples.Length;
                AddToRecording(samples);
                
                if (_silenceCounter >= _silenceTailSamples)
                {
                    return StopRecording();
                }
            }
            else
            {
                // Maintain pre-roll buffer
                AddToPreRoll(samples);
            }
        }

        // Check max duration
        if (_isRecording && _recordingBuffer.Count >= _maxDurationSamples)
        {
            _logger.LogWarning("Transmission exceeded max duration, forcing stop");
            return StopRecording();
        }

        return null;
    }

    private void StartRecording()
    {
        _isRecording = true;
        _recordingBuffer.Clear();
        
        // Add pre-roll buffer to start of recording
        _recordingBuffer.AddRange(_preRollBuffer);
        _preRollBuffer.Clear();
        
        _logger.LogDebug("Started recording transmission with {PreRoll:F2}s pre-roll", 
            (double)_recordingBuffer.Count / _sampleRate);
    }

    private RecordedTransmission? StopRecording()
    {
        _isRecording = false;
        
        if (_recordingBuffer.Count < _minDurationSamples)
        {
            _logger.LogDebug("Discarding short transmission ({Duration:F2}s < {Min:F2}s)", 
                (double)_recordingBuffer.Count / _sampleRate, 
                (double)_minDurationSamples / _sampleRate);
            _recordingBuffer.Clear();
            return null;
        }

        var recording = new RecordedTransmission
        {
            Samples = [.. _recordingBuffer],
            SampleRate = _sampleRate,
            Duration = TimeSpan.FromSeconds((double)_recordingBuffer.Count / _sampleRate),
            Timestamp = DateTime.UtcNow
        };

        _logger.LogInformation("Recorded transmission: {Duration:F2}s", recording.Duration.TotalSeconds);
        _recordingBuffer.Clear();
        
        return recording;
    }

    private void AddToRecording(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _recordingBuffer.Add(sample);
        }
    }

    private void AddToPreRoll(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            if (_preRollBuffer.Count >= _preRollSamples)
            {
                _preRollBuffer.Dequeue();
            }
            _preRollBuffer.Enqueue(sample);
        }
    }

    /// <summary>
    /// Reset recorder state
    /// </summary>
    public void Reset()
    {
        _isRecording = false;
        _silenceCounter = 0;
        _preRollBuffer.Clear();
        _recordingBuffer.Clear();
    }
}

public sealed record RecordedTransmission
{
    public required float[] Samples { get; init; }
    public int SampleRate { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime Timestamp { get; init; }
    public long? Frequency { get; set; }
    
    /// <summary>
    /// Resample to target sample rate (e.g., 16kHz for ASR)
    /// </summary>
    public float[] ResampleTo(int targetSampleRate)
    {
        if (targetSampleRate == SampleRate)
            return Samples;

        var ratio = (double)targetSampleRate / SampleRate;
        var newLength = (int)(Samples.Length * ratio);
        var resampled = new float[newLength];

        for (var i = 0; i < newLength; i++)
        {
            var srcIndex = i / ratio;
            var srcIndexInt = (int)srcIndex;
            var frac = srcIndex - srcIndexInt;

            if (srcIndexInt + 1 < Samples.Length)
            {
                // Linear interpolation
                resampled[i] = (float)(Samples[srcIndexInt] * (1 - frac) + Samples[srcIndexInt + 1] * frac);
            }
            else
            {
                resampled[i] = Samples[srcIndexInt];
            }
        }

        return resampled;
    }
}
