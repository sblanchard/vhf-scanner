# VHF Scanner for IC-705

A C# application that monitors your IC-705's built-in scan function, captures audio when squelch opens, transcribes speech using NVIDIA Parakeet ASR (via Sherpa-ONNX), extracts amateur radio callsigns, and sends notifications to Telegram/Discord.

## How It Works

```
┌──────────────────────────────────────────────────────────────────┐
│                      IC-705 (Built-in Scan)                       │
│  • You program memory channels with VHF frequencies              │
│  • You start Memory Scan (SCAN > MEMO)                           │
│  • Radio scans and STOPS when squelch breaks                     │
└───────────────────────────┬──────────────────────────────────────┘
                            │ CI-V (USB Serial)
                            │ • Poll squelch status (15 01)
                            │ • Read frequency when open (03)
                            ▼
┌──────────────────────────────────────────────────────────────────┐
│                         VHF Scanner App                           │
│                                                                    │
│   CI-V Monitor ──► Audio Capture ──► Parakeet ASR                │
│        │                                   │                      │
│        └── Detects squelch open ───────────┘                      │
│                                            │                      │
│                              Callsign Extractor                   │
│                                            │                      │
│                              Telegram / Discord                   │
└──────────────────────────────────────────────────────────────────┘
```

## Features

- 📻 **Uses IC-705's native scan** - no software frequency stepping
- 🎤 **USB audio capture** - records when squelch opens  
- 🗣️ **NVIDIA Parakeet ASR** - state-of-the-art speech recognition via Sherpa-ONNX
- 📞 **Callsign extraction** - recognizes phonetic alphabet ("Fox Four Juliet Zulu Whiskey")
- 📱 **Notifications** - Telegram, Discord webhooks

## Requirements

- .NET 10.0 SDK
- Icom IC-705 connected via USB
- Memory channels programmed with VHF frequencies

## Quick Start

### 1. Configure

Edit `appsettings.json`:

```json
{
  "Radio": {
    "PortName": "/dev/ttyUSB0",  // or "COM3" on Windows
    "BaudRate": 19200
  },
  "Notifications": {
    "Telegram": {
      "BotToken": "YOUR_BOT_TOKEN",
      "ChatId": "YOUR_CHAT_ID"
    }
  }
}
```

### 2. Program IC-705

1. Store VHF frequencies in memory channels (e.g., 2m repeaters, simplex)
2. Set appropriate squelch level

### 3. Run

```bash
cd src/VhfScanner.App
dotnet run
```

### 4. Start Scanning

On your IC-705:
- Press **SCAN** button
- Touch **MEMO** to start Memory Scan
- The radio will scan through your memory channels and stop when squelch opens

The app will:
1. Detect when squelch opens via CI-V
2. Record the audio transmission
3. Transcribe using Parakeet
4. Extract callsign(s)
5. Send notification

## Configuration

| Setting | Description | Default |
|---------|-------------|---------|
| `Radio:PortName` | Serial port | `/dev/ttyUSB0` |
| `Radio:BaudRate` | CI-V baud rate | `19200` |
| `Audio:DeviceIndex` | Audio device (-1 = auto) | `-1` |
| `Audio:SampleRate` | Capture sample rate | `48000` |
| `Asr:Model` | Parakeet model | `sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8` |
| `Asr:UseGpu` | Use CUDA | `false` |
| `Scanner:PollIntervalMs` | CI-V poll rate | `50` |
| `Scanner:MinCallsignConfidence` | Min confidence to notify | `0.5` |

## Parakeet Models

The app auto-downloads the Parakeet model on first run (~630MB).

Available models:
- `sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8` - English only, fast
- `sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8` - 25 European languages

## Telegram Setup

1. Create bot: Message [@BotFather](https://t.me/BotFather) → `/newbot`
2. Get chat ID: Message your bot, visit `https://api.telegram.org/bot<TOKEN>/getUpdates`
3. For groups: Add bot to group, send a message, check `getUpdates` for negative chat ID

## Example Output

```
[14:32:15 INF] 📻 Squelch OPEN on 145.5000 MHz
[14:32:22 INF] 📼 Recorded 7.2s transmission
[14:32:23 INF] 🎙️ Transcribing 7.2s audio from 145.5000 MHz
[14:32:24 INF] 📝 Transcription: "CQ CQ CQ this is Fox Four Juliet Zulu Whiskey portable"
[14:32:24 INF] 📞 Detected: F4JZW (confidence: 70%, method: Phonetic)
```

## Files

```
src/
├── VhfScanner.Core/
│   ├── Radio/
│   │   ├── CivProtocol.cs       # CI-V protocol implementation
│   │   └── Ic705Controller.cs   # IC-705 control
│   ├── Audio/
│   │   ├── AudioCapture.cs      # NAudio USB capture
│   │   └── TransmissionRecorder.cs
│   ├── Asr/
│   │   ├── IAsrService.cs
│   │   ├── ParakeetAsrService.cs  # Sherpa-ONNX Parakeet
│   │   └── CallsignExtractor.cs
│   ├── Notifications/
│   │   └── NotificationServices.cs
│   └── Scanner/
│       └── VhfScannerService.cs  # Main orchestrator
└── VhfScanner.App/
    ├── Program.cs
    └── appsettings.json
```

## License

MIT

## 73 de F4JZW
