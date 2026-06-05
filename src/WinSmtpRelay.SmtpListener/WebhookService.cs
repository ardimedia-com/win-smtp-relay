using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSmtpRelay.Core.Configuration;

namespace WinSmtpRelay.SmtpListener;

public class WebhookService
{
    private readonly WebhookOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookService> _logger;

    public WebhookService(
        IOptions<WebhookOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<WebhookService> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task NotifyMessageReceivedAsync(
        string messageId, string sender, string recipients, int sizeBytes,
        string? sourceIp, CancellationToken cancellationToken = default)
    {
        if (_options.OnMessageReceived.Count == 0) return;

        var payload = new
        {
            @event = "message.received",
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            messageId,
            sender,
            recipients = recipients.Split(';', StringSplitOptions.RemoveEmptyEntries),
            sizeBytes,
            sourceIp
        };

        var json = JsonSerializer.Serialize(payload);

        var tasks = _options.OnMessageReceived.Select(endpoint =>
            PostWebhookAsync(endpoint, json, cancellationToken));

        await Task.WhenAll(tasks);
    }

    private async Task PostWebhookAsync(WebhookEndpoint endpoint, string json, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Webhook");
            client.Timeout = TimeSpan.FromSeconds(endpoint.TimeoutSeconds);

            var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(endpoint.Secret))
            {
                var signature = ComputeHmacSha256(json, endpoint.Secret);
                request.Headers.Add("X-Webhook-Signature", signature);
            }

            var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Webhook {Url} returned {StatusCode}", endpoint.Url, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Webhook {Url} failed", endpoint.Url);
        }
    }

    private static string ComputeHmacSha256(string payload, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(keyBytes, payloadBytes);
        return $"sha256={Convert.ToHexStringLower(hash)}";
    }
}
