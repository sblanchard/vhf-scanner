using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace VhfScanner.Core.Audio;

/// <summary>
/// Windows audio capture using NAudio (WASAPI)
/// </summary>
public sealed class WindowsAudioCapture : IAudioCapture
{
    private readonly ILogger<WindowsAudioCapture> _logger;
    private readonly int _deviceIndex;
    private readonly int _sampleRate;
    private readonly int _channels;

    private WaveInEvent? _waveIn;
    private readonly Channel<AudioChunk> _audioChannel;
    private bool _isCapturing;

    public ChannelReader<AudioChunk> AudioStream => _audioChannel.Reader;
    public bool IsCapturing => _isCapturing;
    public WaveFormat WaveFormat => new(_sampleRate, 16, _channels);

    public WindowsAudioCapture(
        ILogger<WindowsAudioCapture> logger,
        int deviceIndex = 0,
        int sampleRate = 48000,
        int channels = 1)
    {
        _logger = logger;
        _deviceIndex = deviceIndex;
        _sampleRate = sampleRate;
        _channels = channels;

        _audioChannel = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true
        });
    }

    public void StartCapture()
    {
        if (_isCapturing)
            return;

        _waveIn = new WaveInEvent
        {
            DeviceNumber = _deviceIndex,
            WaveFormat = WaveFormat,
            BufferMilliseconds = 100
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        _waveIn.StartRecording();
        _isCapturing = true;

        _logger.LogInformation("Started audio capture on device {DeviceIndex} at {SampleRate}Hz",
            _deviceIndex, _sampleRate);
    }

    public void StopCapture()
    {
        if (!_isCapturing)
            return;

        _waveIn?.StopRecording();
        _isCapturing = false;

        _logger.LogInformation("Stopped audio capture");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
            return;

        // Convert 16-bit PCM to float32
        var samples = new float[e.BytesRecorded / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = BitConverter.ToInt16(e.Buffer, i * 2);
            samples[i] = sample / 32768f;
        }

        var chunk = new AudioChunk
        {
            Samples = samples,
            Timestamp = DateTime.UtcNow,
            SampleRate = _sampleRate
        };

        // Non-blocking write
        _audioChannel.Writer.TryWrite(chunk);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            _logger.LogError(e.Exception, "Audio recording stopped due to error");
        }
        _isCapturing = false;
    }

    public async ValueTask DisposeAsync()
    {
        StopCapture();
        _waveIn?.Dispose();
        _audioChannel.Writer.Complete();
        await Task.CompletedTask;
    }
}
