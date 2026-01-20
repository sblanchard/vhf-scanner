using System.Formats.Tar;
using System.Runtime.CompilerServices;
using ICSharpCode.SharpZipLib.BZip2;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace VhfScanner.Core.Asr;

/// <summary>
/// Speech-to-text transcription using NVIDIA Parakeet via Sherpa-ONNX
/// Adapted from StationPilot implementation
/// </summary>
public sealed class ParakeetAsrService : IAsrService
{
    private readonly ILogger<ParakeetAsrService> _logger;
    private readonly ParakeetSettings _settings;
    private readonly SemaphoreSlim _processorLock = new(1, 1);
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(30) };

    private OfflineRecognizer? _recognizer;
    private bool _disposed;

    public bool IsReady => _recognizer != null && !_disposed;
    public string? CurrentModel { get; private set; }

    public event EventHandler<double>? DownloadProgress;

    public ParakeetAsrService(ILogger<ParakeetAsrService> logger, ParakeetSettings? settings = null)
    {
        _logger = logger;
        _settings = settings ?? new ParakeetSettings();
    }

    public async Task<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ParakeetAsrService));

        try
        {
            _logger.LogInformation("Initializing Parakeet ASR with model: {Model}", _settings.ModelName);

            var modelPaths = await EnsureModelExistsAsync(_settings.ModelName, ct);
            
            DisposeRecognizer();

            var config = new OfflineRecognizerConfig();
            config.FeatConfig.SampleRate = 16000;
            config.FeatConfig.FeatureDim = 80;

            config.ModelConfig.Transducer.Encoder = modelPaths.Encoder;
            config.ModelConfig.Transducer.Decoder = modelPaths.Decoder;
            config.ModelConfig.Transducer.Joiner = modelPaths.Joiner;
            config.ModelConfig.Tokens = modelPaths.Tokens;
            config.ModelConfig.NumThreads = _settings.Threads > 0 ? _settings.Threads : Environment.ProcessorCount;
            config.ModelConfig.Debug = 0;
            config.ModelConfig.Provider = _settings.UseGpu ? "cuda" : "cpu";

            config.DecodingMethod = "greedy_search";

            _recognizer = new OfflineRecognizer(config);
            CurrentModel = _settings.ModelName;

            _logger.LogInformation("Parakeet initialized successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Parakeet ASR");
            return false;
        }
    }

    public async Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> samples, CancellationToken ct = default)
    {
        if (!IsReady || _recognizer == null)
        {
            _logger.LogWarning("Transcription requested but service not initialized");
            return new TranscriptionResult { Text = string.Empty };
        }

        await _processorLock.WaitAsync(ct);
        try
        {
            var stream = _recognizer.CreateStream();
            stream.AcceptWaveform(16000, samples.ToArray());

            _recognizer.Decode(stream);

            var result = stream.Result;
            var text = result.Text?.Trim() ?? string.Empty;
            var duration = TimeSpan.FromSeconds((double)samples.Length / 16000);

            _logger.LogDebug("Transcribed {Duration:F1}s audio: \"{Text}\"", duration.TotalSeconds, text);

            return new TranscriptionResult
            {
                Text = text,
                Duration = duration,
                Confidence = string.IsNullOrWhiteSpace(text) ? 0 : 0.9
            };
        }
        finally
        {
            _processorLock.Release();
        }
    }

    public async Task<TranscriptionResult> TranscribeFileAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return new TranscriptionResult { Text = string.Empty };

        await using var stream = File.OpenRead(filePath);
        var samples = await ReadWavSamplesAsync(stream, ct);
        
        if (samples == null || samples.Length == 0)
            return new TranscriptionResult { Text = string.Empty };

        return await TranscribeAsync(samples, ct);
    }

    /// <summary>
    /// Transcribe from 16-bit PCM data
    /// </summary>
    public async Task<TranscriptionResult> TranscribePcmAsync(byte[] pcmData, int sampleRate, CancellationToken ct = default)
    {
        var samples = ConvertPcmToFloat(pcmData);
        
        if (sampleRate != 16000)
            samples = ResampleAudio(samples, sampleRate, 16000);

        return await TranscribeAsync(samples, ct);
    }

    private async Task<ParakeetModelPaths> EnsureModelExistsAsync(string modelName, CancellationToken ct)
    {
        var modelsDir = _settings.ModelsDirectory;
        if (!Directory.Exists(modelsDir))
            Directory.CreateDirectory(modelsDir);

        var modelDir = Path.Combine(modelsDir, modelName);
        var paths = GetModelPaths(modelName);

        // Check if model exists
        if (File.Exists(paths.Encoder) && File.Exists(paths.Decoder) && 
            File.Exists(paths.Joiner) && File.Exists(paths.Tokens))
        {
            _logger.LogDebug("Model already exists: {Model}", modelName);
            return paths;
        }

        // Download model
        _logger.LogInformation("Downloading Parakeet model: {Model}", modelName);
        
        var url = $"https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/{modelName}.tar.bz2";
        var archivePath = Path.Combine(modelsDir, $"{modelName}.tar.bz2");

        try
        {
            // Download model archive
            {
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1;
                long bytesRead = 0;

                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];
                int read;
                var lastReport = DateTime.UtcNow;

                while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    bytesRead += read;

                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 500)
                    {
                        var progress = totalBytes > 0 ? (double)bytesRead / totalBytes : 0;
                        _logger.LogDebug("Download progress: {Progress:P0}", progress);
                        DownloadProgress?.Invoke(this, progress * 100);
                        lastReport = DateTime.UtcNow;
                    }
                }

                await fileStream.FlushAsync(ct);
            } // fileStream is disposed here

            _logger.LogInformation("Extracting model archive...");

            // Extract after download stream is closed
            await using var archiveStream = File.OpenRead(archivePath);
            await using var bz2Stream = new BZip2InputStream(archiveStream);
            await TarFile.ExtractToDirectoryAsync(bz2Stream, modelsDir, overwriteFiles: true, cancellationToken: ct);

            _logger.LogInformation("Model extracted: {Model}", modelName);
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                try { File.Delete(archivePath); }
                catch { /* ignore */ }
            }
        }

        return paths;
    }

    private ParakeetModelPaths GetModelPaths(string modelName)
    {
        var dir = Path.Combine(_settings.ModelsDirectory, modelName);
        var isInt8 = modelName.Contains("int8");
        var suffix = isInt8 ? ".int8" : "";

        return new ParakeetModelPaths
        {
            Encoder = Path.Combine(dir, $"encoder{suffix}.onnx"),
            Decoder = Path.Combine(dir, $"decoder{suffix}.onnx"),
            Joiner = Path.Combine(dir, $"joiner{suffix}.onnx"),
            Tokens = Path.Combine(dir, "tokens.txt")
        };
    }

    private static float[] ConvertPcmToFloat(byte[] pcmData)
    {
        var sampleCount = pcmData.Length / 2;
        var samples = new float[sampleCount];

        for (var i = 0; i < sampleCount; i++)
        {
            var pcmValue = (short)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
            samples[i] = pcmValue / 32768f;
        }

        return samples;
    }

    private static float[] ResampleAudio(float[] input, int inputRate, int targetRate)
    {
        if (inputRate == targetRate)
            return input;

        var ratio = (double)inputRate / targetRate;
        var outputLength = (int)(input.Length / ratio);
        var output = new float[outputLength];

        for (var i = 0; i < outputLength; i++)
        {
            var srcPos = i * ratio;
            var srcIndex = (int)srcPos;
            var fraction = (float)(srcPos - srcIndex);

            if (srcIndex + 1 < input.Length)
                output[i] = input[srcIndex] * (1 - fraction) + input[srcIndex + 1] * fraction;
            else if (srcIndex < input.Length)
                output[i] = input[srcIndex];
        }

        return output;
    }

    private static async Task<float[]?> ReadWavSamplesAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        try
        {
            var riff = reader.ReadBytes(4);
            if (System.Text.Encoding.ASCII.GetString(riff) != "RIFF")
                return null;

            reader.ReadInt32();
            var wave = reader.ReadBytes(4);
            if (System.Text.Encoding.ASCII.GetString(wave) != "WAVE")
                return null;

            while (stream.Position < stream.Length)
            {
                ct.ThrowIfCancellationRequested();

                var chunkId = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
                var chunkSize = reader.ReadInt32();

                if (chunkId == "data")
                {
                    var pcmData = reader.ReadBytes(chunkSize);
                    return ConvertPcmToFloat(pcmData);
                }
                
                reader.ReadBytes(chunkSize);
            }
        }
        catch (EndOfStreamException) { }

        return null;
    }

    private void DisposeRecognizer()
    {
        _recognizer?.Dispose();
        _recognizer = null;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        
        _disposed = true;
        DisposeRecognizer();
        _processorLock.Dispose();
        _httpClient.Dispose();
        
        return ValueTask.CompletedTask;
    }
}

public sealed class ParakeetSettings
{
    public string ModelName { get; set; } = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8";
    public string ModelsDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "models");
    public bool UseGpu { get; set; }
    public int Threads { get; set; }
}

internal record ParakeetModelPaths
{
    public required string Encoder { get; init; }
    public required string Decoder { get; init; }
    public required string Joiner { get; init; }
    public required string Tokens { get; init; }
}
