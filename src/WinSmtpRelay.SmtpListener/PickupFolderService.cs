using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using WinSmtpRelay.Core.Configuration;
using WinSmtpRelay.Core.Interfaces;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.SmtpListener;

public class PickupFolderService : BackgroundService
{
    private readonly SmtpListenerOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PickupFolderService> _logger;

    public PickupFolderService(
        IOptions<SmtpListenerOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<PickupFolderService> logger)
    {
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.PickupFolder))
        {
            _logger.LogDebug("Pickup folder not configured, skipping");
            return;
        }

        if (!Directory.Exists(_options.PickupFolder))
        {
            try
            {
                Directory.CreateDirectory(_options.PickupFolder);
                _logger.LogInformation("Created pickup folder: {Folder}", _options.PickupFolder);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create pickup folder: {Folder}", _options.PickupFolder);
                return;
            }
        }

        _logger.LogInformation("Pickup folder watcher started: {Folder} (poll every {Interval}s)",
            _options.PickupFolder, _options.PickupFolderPollIntervalSeconds);

        var pollInterval = TimeSpan.FromSeconds(_options.PickupFolderPollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPickupFilesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error processing pickup folder");
            }

            await Task.Delay(pollInterval, stoppingToken);
        }
    }

    private async Task ProcessPickupFilesAsync(CancellationToken cancellationToken)
    {
        var files = Directory.GetFiles(_options.PickupFolder!, "*.eml");
        if (files.Length == 0) return;

        foreach (var filePath in files)
        {
            try
            {
                await ProcessFileAsync(filePath, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to process pickup file: {File}", Path.GetFileName(filePath));
                MoveToErrorFolder(filePath);
            }
        }
    }

    private async Task ProcessFileAsync(string filePath, CancellationToken cancellationToken)
    {
        var rawMessage = await File.ReadAllBytesAsync(filePath, cancellationToken);

        MimeMessage mimeMessage;
        using (var stream = new MemoryStream(rawMessage))
        {
            mimeMessage = await MimeMessage.LoadAsync(stream, cancellationToken);
        }

        var sender = mimeMessage.From.Mailboxes.FirstOrDefault()?.Address ?? "unknown@localhost";
        var recipients = string.Join(";", mimeMessage.To.Mailboxes.Select(m => m.Address)
            .Concat(mimeMessage.Cc.Mailboxes.Select(m => m.Address))
            .Concat(mimeMessage.Bcc.Mailboxes.Select(m => m.Address)));

        if (string.IsNullOrEmpty(recipients))
        {
            _logger.LogWarning("Pickup file {File} has no recipients, skipping", Path.GetFileName(filePath));
            MoveToErrorFolder(filePath);
            return;
        }

        var message = new QueuedMessage
        {
            MessageId = mimeMessage.MessageId ?? $"pickup-{Guid.NewGuid()}@winsmtprelay",
            Sender = sender,
            Recipients = recipients,
            RawMessage = rawMessage,
            SizeBytes = rawMessage.Length,
            Status = MessageStatus.Queued,
            CreatedUtc = DateTimeOffset.UtcNow,
            SourceIp = "pickup"
        };

        using var scope = _scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IMessageQueue>();
        var id = await queue.EnqueueAsync(message, cancellationToken);

        _logger.LogInformation("Pickup file {File} queued (id={QueueId}) from {Sender} to {Recipients}",
            Path.GetFileName(filePath), id, sender, recipients);

        File.Delete(filePath);
    }

    private void MoveToErrorFolder(string filePath)
    {
        try
        {
            var errorDir = Path.Combine(_options.PickupFolder!, "error");
            Directory.CreateDirectory(errorDir);
            var dest = Path.Combine(errorDir, Path.GetFileName(filePath));
            if (File.Exists(dest))
                dest = Path.Combine(errorDir, $"{Path.GetFileNameWithoutExtension(filePath)}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.eml");
            File.Move(filePath, dest);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to move file to error folder: {File}", filePath);
        }
    }
}
