using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VhfScanner.Core.Asr;
using VhfScanner.Core.Audio;
using VhfScanner.Core.Notifications;
using VhfScanner.Core.Radio;

namespace VhfScanner.Core.Scanner;

/// <summary>
/// VHF Scanner that monitors IC-705's built-in scan function.
/// The radio scans memory channels and stops when squelch opens.
/// This service monitors squelch, records audio, transcribes, and notifies.
/// </summary>
public sealed class VhfScannerService : BackgroundService
{
    private readonly ILogger<VhfScannerService> _logger;
    private readonly Ic705Controller _radio;
    private readonly IAudioCapture _audioCapture;
    private readonly TransmissionRecorder _recorder;
    private readonly IAsrService _asrService;
    private readonly INotificationService _notifier;
    private readonly VhfScannerOptions _options;

    private readonly Channel<RecordedTransmission> _transcriptionQueue;
    private bool _lastSquelchState;
    private long _currentFrequency;
    private DateTime _squelchOpenTime;

    public VhfScannerService(
        ILogger<VhfScannerService> logger,
        Ic705Controller radio,
        IAudioCapture audioCapture,
        TransmissionRecorder recorder,
        IAsrService asrService,
        INotificationService notifier,
        VhfScannerOptions? options = null)
    {
        _logger = logger;
        _radio = radio;
        _audioCapture = audioCapture;
        _recorder = recorder;
        _asrService = asrService;
        _notifier = notifier;
        _options = options ?? new VhfScannerOptions();

        _transcriptionQueue = Channel.CreateBounded<RecordedTransmission>(
            new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.DropOldest });
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting VHF Scanner - monitoring IC-705 squelch status");
        _logger.LogInformation("Start the Memory Scan on your IC-705 (SCAN > MEMO)");

        try
        {
            // Connect to radio
            await _radio.ConnectAsync(ct);
            
            // Initialize ASR if needed
            if (!_asrService.IsReady && _asrService is ParakeetAsrService parakeet)
            {
                _logger.LogInformation("Initializing Parakeet ASR...");
                if (!await parakeet.InitializeAsync(ct))
                {
                    _logger.LogError("Failed to initialize ASR service");
                    return;
                }
            }

            // Start audio capture
            _audioCapture.StartCapture();

            // Start transcription processor in background
            var transcriptionTask = ProcessTranscriptionsAsync(ct);

            // Main monitoring loop
            await MonitorSquelchAsync(ct);

            await transcriptionTask;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Scanner service stopping");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scanner service error");
            throw;
        }
    }

    /// <summary>
    /// Poll CI-V for squelch status and record transmissions
    /// </summary>
    private async Task MonitorSquelchAsync(CancellationToken ct)
    {
        _logger.LogInformation("Monitoring squelch status (polling every {Interval}ms)", _options.PollIntervalMs);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Poll squelch status via CI-V command 15 01
                var squelchOpen = await _radio.IsSquelchOpenAsync(ct);

                // Squelch just opened - transmission starting
                if (squelchOpen && !_lastSquelchState)
                {
                    _squelchOpenTime = DateTime.UtcNow;
                    _currentFrequency = await _radio.ReadFrequencyAsync(ct);
                    
                    _logger.LogInformation("📻 Squelch OPEN on {Frequency:F4} MHz", 
                        _currentFrequency / 1_000_000.0);
                    
                    _recorder.Reset();
                }

                // Process audio while squelch is open (or in hold period)
                if (_audioCapture.AudioStream.TryRead(out var chunk))
                {
                    var recording = _recorder.ProcessSamples(chunk.Samples, squelchOpen);
                    
                    if (recording != null)
                    {
                        // Transmission complete - queue for transcription
                        recording.Frequency = _currentFrequency;
                        await _transcriptionQueue.Writer.WriteAsync(recording, ct);
                        
                        _logger.LogInformation("📼 Recorded {Duration:F1}s transmission", 
                            recording.Duration.TotalSeconds);
                    }
                }

                _lastSquelchState = squelchOpen;
                
                await Task.Delay(_options.PollIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error polling squelch status");
                await Task.Delay(1000, ct);
            }
        }
    }

    /// <summary>
    /// Process recorded transmissions - transcribe and extract callsigns
    /// </summary>
    private async Task ProcessTranscriptionsAsync(CancellationToken ct)
    {
        await foreach (var transmission in _transcriptionQueue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await ProcessTransmissionAsync(transmission, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing transmission");
            }
        }
    }

    private async Task ProcessTransmissionAsync(RecordedTransmission transmission, CancellationToken ct)
    {
        _logger.LogInformation("🎙️ Transcribing {Duration:F1}s audio from {Freq:F4} MHz",
            transmission.Duration.TotalSeconds,
            (transmission.Frequency ?? 0) / 1_000_000.0);

        // Resample to 16kHz for Parakeet
        var samples = transmission.SampleRate != 16000
            ? transmission.ResampleTo(16000)
            : transmission.Samples;

        // Transcribe
        var result = await _asrService.TranscribeAsync(samples, ct);

        if (string.IsNullOrWhiteSpace(result.Text))
        {
            _logger.LogDebug("No speech detected in transmission");
            return;
        }

        _logger.LogInformation("📝 Transcription: \"{Text}\"", result.Text);

        // Extract callsigns
        var callsigns = CallsignExtractor.Extract(result.Text);

        if (callsigns.Count == 0)
        {
            _logger.LogDebug("No callsigns detected in: \"{Text}\"", result.Text);
            return;
        }

        // Send notifications
        foreach (var callsign in callsigns)
        {
            if (callsign.Confidence < _options.MinCallsignConfidence)
            {
                _logger.LogDebug("Skipping low-confidence callsign: {Callsign} ({Confidence:P0})",
                    callsign.Callsign, callsign.Confidence);
                continue;
            }

            _logger.LogInformation("📞 Detected: {Callsign} (confidence: {Confidence:P0}, method: {Method})",
                callsign.Callsign, callsign.Confidence, callsign.ExtractionMethod);

            var activity = new DetectedActivity
            {
                Callsign = callsign.Callsign,
                FrequencyHz = transmission.Frequency ?? 0,
                Timestamp = transmission.Timestamp,
                TransmissionDuration = transmission.Duration,
                TranscribedText = result.Text,
                Confidence = callsign.Confidence
            };

            await _notifier.SendActivityAsync(activity, ct);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping VHF Scanner");
        _audioCapture.StopCapture();
        _transcriptionQueue.Writer.Complete();
        await base.StopAsync(cancellationToken);
    }
}

public sealed class VhfScannerOptions
{
    /// <summary>
    /// How often to poll squelch status via CI-V (milliseconds)
    /// </summary>
    public int PollIntervalMs { get; set; } = 50;

    /// <summary>
    /// Minimum confidence for callsign detection to trigger notification
    /// </summary>
    public double MinCallsignConfidence { get; set; } = 0.5;
}

