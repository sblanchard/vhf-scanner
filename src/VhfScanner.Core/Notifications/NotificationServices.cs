using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VhfScanner.Core.Notifications;

/// <summary>
/// Interface for notification services
/// </summary>
public interface INotificationService
{
    Task SendActivityAsync(DetectedActivity activity, CancellationToken ct = default);
    string ServiceName { get; }
}

/// <summary>
/// Represents a detected radio activity
/// </summary>
public sealed record DetectedActivity
{
    public required string Callsign { get; init; }
    public required long FrequencyHz { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public TimeSpan TransmissionDuration { get; init; }
    public string? TranscribedText { get; init; }
    public double Confidence { get; init; }
    public string? FrequencyName { get; init; }
    
    public double FrequencyMHz => FrequencyHz / 1_000_000.0;
}

/// <summary>
/// Telegram Bot notification service
/// </summary>
public sealed class TelegramNotifier : INotificationService, IDisposable
{
    private readonly ILogger<TelegramNotifier> _logger;
    private readonly HttpClient _http;
    private readonly string _botToken;
    private readonly string _chatId;

    public string ServiceName => "Telegram";

    public TelegramNotifier(
        ILogger<TelegramNotifier> logger,
        string botToken,
        string chatId)
    {
        _logger = logger;
        _botToken = botToken;
        _chatId = chatId;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.telegram.org/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task SendActivityAsync(DetectedActivity activity, CancellationToken ct = default)
    {
        var freqDisplay = string.IsNullOrEmpty(activity.FrequencyName)
            ? $"{activity.FrequencyMHz:F4} MHz"
            : $"{activity.FrequencyName} ({activity.FrequencyMHz:F4} MHz)";

        var message = $"""
            📻 *VHF Activity Detected*
            
            🔊 Callsign: `{EscapeMarkdown(activity.Callsign)}`
            📡 Frequency: `{EscapeMarkdown(freqDisplay)}`
            🕐 Time: {activity.Timestamp:yyyy-MM-dd HH:mm:ss} UTC
            ⏱️ Duration: {activity.TransmissionDuration.TotalSeconds:F1}s
            📊 Confidence: {activity.Confidence:P0}
            """;

        if (!string.IsNullOrWhiteSpace(activity.TranscribedText))
        {
            message += $"\n\n💬 _\"{EscapeMarkdown(Truncate(activity.TranscribedText, 200))}\"_";
        }

        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = _chatId,
                ["text"] = message,
                ["parse_mode"] = "Markdown"
            });

            var response = await _http.PostAsync($"bot{_botToken}/sendMessage", content, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Telegram API error: {Status} - {Error}", response.StatusCode, error);
            }
            else
            {
                _logger.LogDebug("Sent Telegram notification for {Callsign}", activity.Callsign);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram notification");
        }
    }

    private static string EscapeMarkdown(string text)
    {
        // Escape Markdown special characters
        return text
            .Replace("_", "\\_")
            .Replace("*", "\\*")
            .Replace("[", "\\[")
            .Replace("]", "\\]")
            .Replace("`", "\\`");
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Generic webhook notification service (for Discord, Slack, custom endpoints)
/// </summary>
public sealed class WebhookNotifier : INotificationService, IDisposable
{
    private readonly ILogger<WebhookNotifier> _logger;
    private readonly HttpClient _http;
    private readonly string _webhookUrl;
    private readonly WebhookFormat _format;

    public string ServiceName => "Webhook";

    public WebhookNotifier(
        ILogger<WebhookNotifier> logger,
        string webhookUrl,
        WebhookFormat format = WebhookFormat.Json)
    {
        _logger = logger;
        _webhookUrl = webhookUrl;
        _format = format;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task SendActivityAsync(DetectedActivity activity, CancellationToken ct = default)
    {
        try
        {
            HttpContent content = _format switch
            {
                WebhookFormat.Discord => CreateDiscordContent(activity),
                WebhookFormat.Slack => CreateSlackContent(activity),
                _ => JsonContent.Create(activity)
            };

            var response = await _http.PostAsync(_webhookUrl, content, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Webhook error: {Status} - {Error}", response.StatusCode, error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send webhook notification");
        }
    }

    private static HttpContent CreateDiscordContent(DetectedActivity activity)
    {
        var payload = new
        {
            embeds = new[]
            {
                new
                {
                    title = "📻 VHF Activity Detected",
                    color = 0x00FF00,
                    fields = new[]
                    {
                        new { name = "Callsign", value = activity.Callsign, inline = true },
                        new { name = "Frequency", value = $"{activity.FrequencyMHz:F4} MHz", inline = true },
                        new { name = "Duration", value = $"{activity.TransmissionDuration.TotalSeconds:F1}s", inline = true },
                        new { name = "Time (UTC)", value = activity.Timestamp.ToString("HH:mm:ss"), inline = true }
                    },
                    footer = new { text = $"Confidence: {activity.Confidence:P0}" }
                }
            }
        };
        return JsonContent.Create(payload);
    }

    private static HttpContent CreateSlackContent(DetectedActivity activity)
    {
        var payload = new
        {
            text = $"📻 *VHF Activity*: {activity.Callsign} on {activity.FrequencyMHz:F4} MHz",
            blocks = new object[]
            {
                new
                {
                    type = "section",
                    text = new
                    {
                        type = "mrkdwn",
                        text = $"*Callsign:* `{activity.Callsign}`\n*Frequency:* {activity.FrequencyMHz:F4} MHz\n*Time:* {activity.Timestamp:HH:mm:ss} UTC"
                    }
                }
            }
        };
        return JsonContent.Create(payload);
    }

    public void Dispose() => _http.Dispose();
}

public enum WebhookFormat
{
    Json,
    Discord,
    Slack
}

/// <summary>
/// Composite notifier that sends to multiple services
/// </summary>
public sealed class CompositeNotifier : INotificationService
{
    private readonly IReadOnlyList<INotificationService> _notifiers;
    private readonly ILogger<CompositeNotifier> _logger;

    public string ServiceName => "Composite";

    public CompositeNotifier(ILogger<CompositeNotifier> logger, IEnumerable<INotificationService> notifiers)
    {
        _logger = logger;
        _notifiers = notifiers.ToList();
    }

    public async Task SendActivityAsync(DetectedActivity activity, CancellationToken ct = default)
    {
        var tasks = _notifiers.Select(n => SendWithLogging(n, activity, ct));
        await Task.WhenAll(tasks);
    }

    private async Task SendWithLogging(INotificationService notifier, DetectedActivity activity, CancellationToken ct)
    {
        try
        {
            await notifier.SendActivityAsync(activity, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send notification via {Service}", notifier.ServiceName);
        }
    }
}
