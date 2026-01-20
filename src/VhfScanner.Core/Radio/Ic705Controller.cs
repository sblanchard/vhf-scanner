using System.IO.Ports;
using Microsoft.Extensions.Logging;

namespace VhfScanner.Core.Radio;

/// <summary>
/// Controller for Icom IC-705 via CI-V protocol over USB serial
/// </summary>
public sealed class Ic705Controller : IAsyncDisposable
{
    private readonly ILogger<Ic705Controller> _logger;
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly byte _radioAddress;
    
    private SerialPort? _serialPort;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly byte[] _readBuffer = new byte[256];
    
    public bool IsConnected => _serialPort?.IsOpen ?? false;
    public long CurrentFrequency { get; private set; }
    public RadioMode CurrentMode { get; private set; }
    
    public event EventHandler<SquelchEventArgs>? SquelchChanged;

    public Ic705Controller(
        ILogger<Ic705Controller> logger, 
        string portName = "/dev/ttyUSB0",
        int baudRate = 19200,
        byte radioAddress = CivProtocol.Ic705DefaultAddress)
    {
        _logger = logger;
        _portName = portName;
        _baudRate = baudRate;
        _radioAddress = radioAddress;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_serialPort?.IsOpen == true)
            return;

        _serialPort = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 1000,
            WriteTimeout = 1000,
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false
        };

        try
        {
            _serialPort.Open();
            _logger.LogInformation("Connected to IC-705 on {Port} at {BaudRate} baud", _portName, _baudRate);
            
            // Read current frequency to verify connection
            var freq = await ReadFrequencyAsync(ct);
            _logger.LogInformation("IC-705 current frequency: {Frequency:F4} MHz", freq / 1_000_000.0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to IC-705 on {Port}", _portName);
            _serialPort?.Dispose();
            _serialPort = null;
            throw;
        }
    }

    public async Task<long> SetFrequencyAsync(long frequencyHz, CancellationToken ct = default)
    {
        await _commandLock.WaitAsync(ct);
        try
        {
            var freqData = CivProtocol.EncodeFrequency(frequencyHz);
            var command = CivProtocol.BuildCommand(CivProtocol.CmdSetFrequency, data: freqData, radioAddress: _radioAddress);
            
            await SendCommandAsync(command, ct);
            var response = await ReadResponseAsync(ct);
            
            if (response?.IsOk == true)
            {
                CurrentFrequency = frequencyHz;
                _logger.LogDebug("Set frequency to {Frequency:F4} MHz", frequencyHz / 1_000_000.0);
                return frequencyHz;
            }
            
            _logger.LogWarning("Failed to set frequency to {Frequency:F4} MHz", frequencyHz / 1_000_000.0);
            return CurrentFrequency;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task<long> ReadFrequencyAsync(CancellationToken ct = default)
    {
        await _commandLock.WaitAsync(ct);
        try
        {
            var command = CivProtocol.BuildCommand(CivProtocol.CmdReadFrequency, radioAddress: _radioAddress);
            await SendCommandAsync(command, ct);
            
            var response = await ReadResponseAsync(ct);
            
            if (response?.Command == CivProtocol.CmdReadFrequency && response.Value.Data.Length >= 5)
            {
                CurrentFrequency = CivProtocol.DecodeFrequency(response.Value.Data);
                return CurrentFrequency;
            }
            
            return CurrentFrequency;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task<bool> SetModeAsync(RadioMode mode, CancellationToken ct = default)
    {
        await _commandLock.WaitAsync(ct);
        try
        {
            // Mode data: mode byte + filter (01 = default)
            var modeData = new byte[] { (byte)mode, 0x01 };
            var command = CivProtocol.BuildCommand(CivProtocol.CmdSetMode, data: modeData, radioAddress: _radioAddress);
            
            await SendCommandAsync(command, ct);
            var response = await ReadResponseAsync(ct);
            
            if (response?.IsOk == true)
            {
                CurrentMode = mode;
                _logger.LogDebug("Set mode to {Mode}", mode);
                return true;
            }
            
            return false;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task<int> ReadSMeterAsync(CancellationToken ct = default)
    {
        await _commandLock.WaitAsync(ct);
        try
        {
            var command = CivProtocol.BuildCommand(CivProtocol.CmdReadSquelchStatus, CivProtocol.SubCmdSMeter, radioAddress: _radioAddress);
            await SendCommandAsync(command, ct);
            
            var response = await ReadResponseAsync(ct);
            
            if (response?.Command == CivProtocol.CmdReadSquelchStatus && response.Value.Data.Length >= 3)
            {
                // S-meter value is BCD encoded in 2 bytes after subcommand
                var high = response.Value.Data[1];
                var low = response.Value.Data[2];
                return (high << 8) | low;
            }
            
            return 0;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task<bool> IsSquelchOpenAsync(CancellationToken ct = default)
    {
        await _commandLock.WaitAsync(ct);
        try
        {
            var command = CivProtocol.BuildCommand(CivProtocol.CmdReadSquelchStatus, CivProtocol.SubCmdSquelchStatus, radioAddress: _radioAddress);
            await SendCommandAsync(command, ct);
            
            var response = await ReadResponseAsync(ct);
            
            if (response?.Command == CivProtocol.CmdReadSquelchStatus && response.Value.Data.Length >= 2)
            {
                // Squelch status: 00 = closed, 01 = open
                return response.Value.Data[1] == 0x01;
            }
            
            return false;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task SendCommandAsync(byte[] command, CancellationToken ct)
    {
        if (_serialPort?.IsOpen != true)
            throw new InvalidOperationException("Serial port is not open");

        await _serialPort.BaseStream.WriteAsync(command, ct);
        await _serialPort.BaseStream.FlushAsync(ct);
        
        _logger.LogTrace("Sent CI-V command: {Command}", Convert.ToHexString(command));
    }

    private async Task<CivResponse?> ReadResponseAsync(CancellationToken ct)
    {
        if (_serialPort?.IsOpen != true)
            return null;

        try
        {
            // Small delay to allow radio to respond
            await Task.Delay(50, ct);
            
            var bytesRead = 0;
            var totalBytesRead = 0;
            
            while (_serialPort.BytesToRead > 0 && totalBytesRead < _readBuffer.Length)
            {
                bytesRead = await _serialPort.BaseStream.ReadAsync(
                    _readBuffer.AsMemory(totalBytesRead, _readBuffer.Length - totalBytesRead), ct);
                totalBytesRead += bytesRead;
                
                // Check if we have a complete frame
                for (var i = 0; i < totalBytesRead; i++)
                {
                    if (_readBuffer[i] == CivProtocol.EndOfMessage)
                    {
                        var response = CivProtocol.ParseResponse(_readBuffer.AsSpan(0, totalBytesRead));
                        _logger.LogTrace("Received CI-V response: {Response}", Convert.ToHexString(_readBuffer.AsSpan(0, totalBytesRead)));
                        return response;
                    }
                }
            }
            
            if (totalBytesRead > 0)
            {
                _logger.LogTrace("Partial CI-V data: {Data}", Convert.ToHexString(_readBuffer.AsSpan(0, totalBytesRead)));
            }
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("CI-V response timeout");
        }
        
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_serialPort?.IsOpen == true)
        {
            _serialPort.Close();
        }
        _serialPort?.Dispose();
        _commandLock.Dispose();
        await Task.CompletedTask;
    }
}

public class SquelchEventArgs : EventArgs
{
    public bool IsOpen { get; init; }
    public long Frequency { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
