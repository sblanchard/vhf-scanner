using Microsoft.Extensions.Logging;
using NAudio.Wave;
using PortAudioSharp;

namespace VhfScanner.Core.Audio;

/// <summary>
/// Factory for creating platform-specific audio capture instances
/// </summary>
public static class AudioDevices
{
    private static readonly bool IsWindows = OperatingSystem.IsWindows();

    /// <summary>
    /// Create appropriate audio capture for current platform
    /// </summary>
    public static IAudioCapture CreateCapture(
        ILoggerFactory loggerFactory,
        int deviceIndex = 0,
        int sampleRate = 48000,
        int channels = 1)
    {
        if (IsWindows)
        {
            var logger = loggerFactory.CreateLogger<WindowsAudioCapture>();
            return new WindowsAudioCapture(logger, deviceIndex, sampleRate, channels);
        }
        else
        {
            var logger = loggerFactory.CreateLogger<LinuxAudioCapture>();
            return new LinuxAudioCapture(logger, deviceIndex, sampleRate, channels);
        }
    }

    /// <summary>
    /// List available audio input devices
    /// </summary>
    public static IEnumerable<AudioDeviceInfo> GetDevices()
    {
        if (IsWindows)
            return GetWindowsDevices();
        else
            return GetLinuxDevices();
    }

    /// <summary>
    /// Find IC-705 audio device by name
    /// </summary>
    public static int? FindIc705Device()
    {
        if (IsWindows)
            return FindWindowsIc705Device();
        else
            return FindLinuxIc705Device();
    }

    private static IEnumerable<AudioDeviceInfo> GetWindowsDevices()
    {
        // Device index 0 is typically the default on Windows
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            yield return new AudioDeviceInfo
            {
                Index = i,
                Name = caps.ProductName,
                Channels = caps.Channels,
                IsDefault = i == 0  // First device is typically default
            };
        }
    }

    private static IEnumerable<AudioDeviceInfo> GetLinuxDevices()
    {
        try
        {
            PortAudio.Initialize();

            var defaultInput = PortAudio.DefaultInputDevice;

            for (var i = 0; i < PortAudio.DeviceCount; i++)
            {
                var info = PortAudio.GetDeviceInfo(i);

                // Only include input devices (recording capable)
                if (info.maxInputChannels > 0)
                {
                    yield return new AudioDeviceInfo
                    {
                        Index = i,
                        Name = info.name ?? $"Device {i}",
                        Channels = info.maxInputChannels,
                        IsDefault = i == defaultInput
                    };
                }
            }
        }
        finally
        {
            try { PortAudio.Terminate(); } catch { /* Ignore termination errors */ }
        }
    }

    private static int? FindWindowsIc705Device()
    {
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);

            // IC-705 typically shows as "USB Audio CODEC" or similar
            if (caps.ProductName.Contains("IC-705", StringComparison.OrdinalIgnoreCase) ||
                caps.ProductName.Contains("ICOM", StringComparison.OrdinalIgnoreCase) ||
                caps.ProductName.Contains("USB Audio CODEC", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return null;
    }

    private static int? FindLinuxIc705Device()
    {
        try
        {
            PortAudio.Initialize();

            for (var i = 0; i < PortAudio.DeviceCount; i++)
            {
                var info = PortAudio.GetDeviceInfo(i);

                // Only check input devices
                if (info.maxInputChannels == 0)
                    continue;

                var name = info.name ?? "";

                // IC-705 typically shows as "USB Audio CODEC" or similar
                if (name.Contains("IC-705", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("ICOM", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("USB Audio CODEC", StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return null;
        }
        finally
        {
            try { PortAudio.Terminate(); } catch { /* Ignore termination errors */ }
        }
    }
}

public sealed record AudioChunk
{
    public required float[] Samples { get; init; }
    public DateTime Timestamp { get; init; }
    public int SampleRate { get; init; }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
}

public sealed record AudioDeviceInfo
{
    public int Index { get; init; }
    public required string Name { get; init; }
    public int Channels { get; init; }
    public bool IsDefault { get; init; }
}
