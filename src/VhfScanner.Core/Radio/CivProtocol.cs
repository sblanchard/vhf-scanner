namespace VhfScanner.Core.Radio;

/// <summary>
/// Icom CI-V protocol constants and helpers for IC-705
/// </summary>
public static class CivProtocol
{
    public const byte Preamble = 0xFE;
    public const byte EndOfMessage = 0xFD;
    public const byte DefaultControllerAddress = 0xE0;
    public const byte Ic705DefaultAddress = 0xA4;
    
    // CI-V Commands
    public const byte CmdSetFrequency = 0x05;
    public const byte CmdReadFrequency = 0x03;
    public const byte CmdSetMode = 0x06;
    public const byte CmdReadMode = 0x04;
    public const byte CmdReadSquelchStatus = 0x15;
    public const byte CmdSetSquelch = 0x14;
    
    // Sub-commands for 0x15 (Read)
    public const byte SubCmdSquelchStatus = 0x01;
    public const byte SubCmdSMeter = 0x02;
    
    // Sub-commands for 0x14 (Set)
    public const byte SubCmdSquelchLevel = 0x03;

    /// <summary>
    /// Build a CI-V command frame
    /// </summary>
    public static byte[] BuildCommand(
        byte command, 
        byte subCommand = 0x00, 
        ReadOnlySpan<byte> data = default,
        byte radioAddress = Ic705DefaultAddress)
    {
        var hasSubCommand = subCommand != 0x00;
        var length = 4 + 1 + (hasSubCommand ? 1 : 0) + data.Length + 1;
        var buffer = new byte[length];
        var index = 0;
        
        buffer[index++] = Preamble;
        buffer[index++] = Preamble;
        buffer[index++] = radioAddress;
        buffer[index++] = DefaultControllerAddress;
        buffer[index++] = command;
        
        if (hasSubCommand)
            buffer[index++] = subCommand;
            
        data.CopyTo(buffer.AsSpan(index));
        index += data.Length;
        
        buffer[index] = EndOfMessage;
        return buffer;
    }

    /// <summary>
    /// Encode frequency in Hz to BCD format (LSB first, 5 bytes)
    /// </summary>
    public static byte[] EncodeFrequency(long frequencyHz)
    {
        Span<byte> bcd = stackalloc byte[5];
        var freq = frequencyHz;
        
        for (var i = 0; i < 5; i++)
        {
            var lowNibble = (byte)(freq % 10);
            freq /= 10;
            var highNibble = (byte)(freq % 10);
            freq /= 10;
            bcd[i] = (byte)((highNibble << 4) | lowNibble);
        }
        
        return bcd.ToArray();
    }

    /// <summary>
    /// Decode BCD frequency (LSB first) to Hz
    /// </summary>
    public static long DecodeFrequency(ReadOnlySpan<byte> bcd)
    {
        if (bcd.Length < 5)
            return 0;
            
        long freq = 0;
        long multiplier = 1;
        
        for (var i = 0; i < 5; i++)
        {
            freq += (bcd[i] & 0x0F) * multiplier;
            multiplier *= 10;
            freq += ((bcd[i] >> 4) & 0x0F) * multiplier;
            multiplier *= 10;
        }
        
        return freq;
    }

    /// <summary>
    /// Parse a CI-V response frame
    /// </summary>
    public static CivResponse? ParseResponse(ReadOnlySpan<byte> data)
    {
        // Minimum valid frame: FE FE to from cmd FD = 6 bytes
        if (data.Length < 6)
            return null;

        // Find start of frame
        var startIndex = -1;
        for (var i = 0; i < data.Length - 1; i++)
        {
            if (data[i] == Preamble && data[i + 1] == Preamble)
            {
                startIndex = i;
                break;
            }
        }

        if (startIndex < 0)
            return null;

        // Find end of frame
        var endIndex = -1;
        for (var i = startIndex + 4; i < data.Length; i++)
        {
            if (data[i] == EndOfMessage)
            {
                endIndex = i;
                break;
            }
        }

        if (endIndex < 0)
            return null;

        var frame = data.Slice(startIndex, endIndex - startIndex + 1);
        
        return new CivResponse
        {
            ToAddress = frame[2],
            FromAddress = frame[3],
            Command = frame[4],
            Data = frame.Length > 6 ? frame.Slice(5, frame.Length - 6).ToArray() : []
        };
    }
}

public readonly record struct CivResponse
{
    public byte ToAddress { get; init; }
    public byte FromAddress { get; init; }
    public byte Command { get; init; }
    public byte[] Data { get; init; }
    
    /// <summary>
    /// Check if this is a positive acknowledgment (OK)
    /// </summary>
    public bool IsOk => Command == 0xFB;
    
    /// <summary>
    /// Check if this is a negative acknowledgment (NG)
    /// </summary>
    public bool IsNg => Command == 0xFA;
}

/// <summary>
/// Operating modes for IC-705
/// </summary>
public enum RadioMode : byte
{
    Lsb = 0x00,
    Usb = 0x01,
    Am = 0x02,
    Cw = 0x03,
    Rtty = 0x04,
    Fm = 0x05,
    WFm = 0x06,
    CwR = 0x07,
    RttyR = 0x08,
    Dv = 0x17,
    Dd = 0x22
}
