namespace WinSmtpRelay.Core.Models;

/// <summary>One recipient reported in a delivery status notification, with the last reply it got.</summary>
/// <param name="Address">The recipient address.</param>
/// <param name="StatusCode">The SMTP reply code ("550", "451"), or null when no attempt reached a server.</param>
/// <param name="StatusMessage">The server's reply text or the relay's reason.</param>
/// <param name="RemoteServer">The server that answered ("host:port"), when known.</param>
/// <param name="Suppressed">Not attempted because the address is on the suppression list.</param>
public sealed record DsnRecipient(string Address, string? StatusCode, string? StatusMessage, string? RemoteServer, bool Suppressed = false);
