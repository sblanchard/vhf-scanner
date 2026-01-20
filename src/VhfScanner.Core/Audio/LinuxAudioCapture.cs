using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PortAudioSharp;

namespace VhfScanner.Core.Audio;

/// <summary>
/// Linux audio capture using PortAudio
/// </summary>
public sealed class LinuxAudioCapture : IAudioCapture
{
    private readonly ILogger<LinuxAudioCapture> _logger;
    private readonly int _deviceIndex;
    private readonly int _sampleRate;
    private readonly int _channels;

    private PortAudioSharp.Stream? _stream;
    private readonly Channel<AudioChunk> _audioChannel;
    private bool _isCapturing;
    private float[]? _captureBuffer;

    public ChannelReader<AudioChunk> AudioStream => _audioChannel.Reader;
    public bool IsCapturing => _isCapturing;

    public LinuxAudioCapture(
        ILogger<LinuxAudioCapture> logger,
        int deviceIndex = 0,
        int sampleRate = 48000,  // 48kHz for best Linux compatibility
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

        try
        {
            PortAudio.Initialize();

            var deviceInfo = PortAudio.GetDeviceInfo(_deviceIndex);

            // Create input stream parameters
            var inputParams = new StreamParameters
            {
                device = _deviceIndex,
                channelCount = _channels,
                sampleFormat = SampleFormat.Float32,
                suggestedLatency = deviceInfo.defaultLowInputLatency
            };

            // Allocate capture buffer (1024 frames)
            const int framesPerBuffer = 1024;
            _captureBuffer = new float[framesPerBuffer * _channels];

            // Open stream with callback
            _stream = new PortAudioSharp.Stream(
                inParams: inputParams,
                outParams: null,
                sampleRate: _sampleRate,
                framesPerBuffer: (uint)framesPerBuffer,
                streamFlags: StreamFlags.ClipOff,
                callback: OnAudioCallback,
                userData: IntPtr.Zero
            );

            _stream.Start();
            _isCapturing = true;

            _logger.LogInformation(
                "Started audio capture on device {DeviceIndex} ({DeviceName}) at {SampleRate}Hz, {Channels} channel(s)",
                _deviceIndex,
                deviceInfo.name,
                _sampleRate,
                _channels);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start audio capture on device {DeviceIndex}", _deviceIndex);
            _isCapturing = false;
            throw;
        }
    }

    public void StopCapture()
    {
        if (!_isCapturing)
            return;

        try
        {
            _stream?.Stop();
            _stream?.Dispose();
            _stream = null;

            PortAudio.Terminate();

            _isCapturing = false;
            _logger.LogInformation("Stopped audio capture");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping audio capture");
        }
    }

    private StreamCallbackResult OnAudioCallback(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userData)
    {
        try
        {
            if (_captureBuffer == null || input == IntPtr.Zero)
                return StreamCallbackResult.Continue;

            // Copy from native buffer to managed array
            var sampleCount = (int)(frameCount * _channels);
            unsafe
            {
                fixed (float* destPtr = _captureBuffer)
                {
                    Buffer.MemoryCopy(
                        (void*)input,
                        destPtr,
                        _captureBuffer.Length * sizeof(float),
                        sampleCount * sizeof(float)
                    );
                }
            }

            // Create copy of samples for channel (avoid mutation)
            var samples = new float[sampleCount];
            Array.Copy(_captureBuffer, samples, sampleCount);

            var chunk = new AudioChunk
            {
                Samples = samples,
                Timestamp = DateTime.UtcNow,
                SampleRate = _sampleRate
            };

            // Non-blocking write
            _audioChannel.Writer.TryWrite(chunk);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in audio callback");
        }

        return StreamCallbackResult.Continue;
    }

    public async ValueTask DisposeAsync()
    {
        StopCapture();
        _audioChannel.Writer.Complete();
        await Task.CompletedTask;
    }
}
