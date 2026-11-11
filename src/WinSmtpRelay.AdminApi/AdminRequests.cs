using System.Globalization;
using System.Net;
using WinSmtpRelay.Core;
using WinSmtpRelay.Core.Models;

namespace WinSmtpRelay.AdminApi;

// Request bodies of the configuration endpoints. Entities are never bound directly (M19): the client cannot set
// Id, TenantId or CreatedUtc, and every field is validated before it reaches a service. Each Validate returns the
// first problem as an operator-facing message (→ 400), or null.

/// <summary>Receive connector (host-level listener).</summary>
public sealed record ReceiveConnectorRequest(
    string Name, string Address = "0.0.0.0", int Port = 25, bool RequireTls = false, bool ImplicitTls = false,
    bool RequireAuth = false, int MaxMessageSizeBytes = 25 * 1024 * 1024, int MaxConnections = 100, bool IsEnabled = true)
{
    public string? Validate() =>
        AdminRequestValidation.Required(Name, "Name")
        ?? (IPAddress.TryParse(Address?.Trim(), out _) ? null : $"Address '{Address}' is not an IP address.")
        ?? AdminRequestValidation.Port(Port, "Port")
        ?? (MaxMessageSizeBytes is >= 1024 and <= int.MaxValue ? null : "MaxMessageSizeBytes must be at least 1024.")
        ?? (MaxConnections is >= 1 and <= 100_000 ? null : "MaxConnections must be between 1 and 100000.");

    public ReceiveConnector ToEntity() => Apply(new ReceiveConnector());

    public ReceiveConnector Apply(ReceiveConnector c)
    {
        c.Name = Name.Trim();
        c.Address = Address.Trim();
        c.Port = Port;
        c.RequireTls = RequireTls;
        c.ImplicitTls = ImplicitTls;
        c.RequireAuth = RequireAuth;
        c.MaxMessageSizeBytes = MaxMessageSizeBytes;
        c.MaxConnections = MaxConnections;
        c.IsEnabled = IsEnabled;
        return c;
    }
}

/// <summary>IP access rule. <c>Network</c> is an address or CIDR network (IPv4 or IPv6).</summary>
public sealed record IpAccessRuleRequest(string Network, IpAccessAction Action = IpAccessAction.Allow, int SortOrder = 0, string? Description = null)
{
    public string? Validate() =>
        AdminRequestValidation.Network(Network)
        ?? (Enum.IsDefined(Action) ? null : "Action must be Allow or Deny.")
        ?? AdminRequestValidation.MaxLength(Description, 500, "Description");

    public IpAccessRule ToEntity() => new() { Network = Network.Trim(), Action = Action, SortOrder = SortOrder, Description = Description?.Trim() };
}

/// <summary>
/// Send connector. <c>Password</c> on PUT: null keeps the stored smart-host password, an empty string clears it,
/// any other value replaces it. It is never returned (responses carry <c>HasPassword</c>).
/// </summary>
public sealed record SendConnectorRequest(
    string Name, string? SmartHost = null, int SmartHostPort = 587, string? Username = null, string? Password = null,
    bool OpportunisticTls = true, bool RequireTls = false, bool IsDefault = false, int MaxConcurrentDeliveries = 4,
    int MaxRetryHours = 48, string RetryIntervalsMinutes = "1,5,30,120,480,1440", int ConnectTimeoutSeconds = 30,
    bool IsEnabled = true, string? EhloDomain = null)
{
    public string? Validate() =>
        AdminRequestValidation.Required(Name, "Name")
        ?? (string.IsNullOrWhiteSpace(SmartHost) ? null : AdminRequestValidation.HostName(SmartHost, "SmartHost"))
        ?? AdminRequestValidation.Port(SmartHostPort, "SmartHostPort")
        ?? (MaxConcurrentDeliveries is >= 1 and <= 100 ? null : "MaxConcurrentDeliveries must be between 1 and 100.")
        ?? (MaxRetryHours is >= 0 and <= 720 ? null : "MaxRetryHours must be between 0 and 720.")
        ?? AdminRequestValidation.RetryIntervals(RetryIntervalsMinutes)
        ?? (ConnectTimeoutSeconds is >= 1 and <= 600 ? null : "ConnectTimeoutSeconds must be between 1 and 600.")
        ?? (string.IsNullOrWhiteSpace(EhloDomain) ? null : AdminRequestValidation.HostName(EhloDomain, "EhloDomain"))
        ?? AdminRequestValidation.NoLineBreaks(Username, "Username")
        ?? AdminRequestValidation.NoLineBreaks(Password, "Password");

    /// <summary>The entity for the service; <c>EncryptedPassword</c> carries the plaintext (encrypted at rest by EF).</summary>
    public SendConnector ToEntity() => new()
    {
        Name = Name.Trim(),
        SmartHost = string.IsNullOrWhiteSpace(SmartHost) ? null : SmartHost.Trim(),
        SmartHostPort = SmartHostPort,
        Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
        EncryptedPassword = Password,
        OpportunisticTls = OpportunisticTls,
        RequireTls = RequireTls,
        IsDefault = IsDefault,
        MaxConcurrentDeliveries = MaxConcurrentDeliveries,
        MaxRetryHours = MaxRetryHours,
        RetryIntervalsMinutes = RetryIntervalsMinutes.Trim(),
        ConnectTimeoutSeconds = ConnectTimeoutSeconds,
        IsEnabled = IsEnabled,
        EhloDomain = string.IsNullOrWhiteSpace(EhloDomain) ? null : EhloDomain.Trim().TrimEnd('.'),
    };
}

/// <summary>Domain route: recipient-domain pattern (e.g. <c>example.com</c> or <c>*.example.com</c>) → send connector.</summary>
public sealed record DomainRouteRequest(string DomainPattern, int SendConnectorId, int SortOrder = 0)
{
    public string? Validate() =>
        AdminRequestValidation.Required(DomainPattern, "DomainPattern")
        ?? AdminRequestValidation.HostName(DomainPattern.Trim().TrimStart('*').TrimStart('.'), "DomainPattern")
        ?? (SendConnectorId > 0 ? null : "SendConnectorId is required.");

    public DomainRoute ToEntity() => new() { DomainPattern = DomainPattern.Trim().ToLowerInvariant(), SendConnectorId = SendConnectorId, SortOrder = SortOrder };
}

/// <summary>
/// Raw DKIM signing configuration (host admins). On PUT a null <c>PrivateKeyPem</c> / <c>PrivateKeyPath</c> keeps
/// the stored value, an empty string clears it. The key material is never returned.
/// </summary>
public sealed record DkimDomainRequest(string Domain, string Selector, string? PrivateKeyPem = null, string? PrivateKeyPath = null, bool IsEnabled = true)
{
    public string? Validate() =>
        AdminRequestValidation.HostName(Domain, "Domain")
        ?? AdminRequestValidation.Selector(Selector)
        ?? (PrivateKeyPem is { Length: > 0 } pem && !pem.Contains("PRIVATE KEY", StringComparison.Ordinal)
            ? "PrivateKeyPem must be a PEM-encoded private key." : null);
}

/// <summary>Host-global SMTP rate limits. 0 disables a limit.</summary>
public sealed record RateLimitSettingsRequest(
    int MaxConnectionsPerIpPerMinute, int MaxMessagesPerSenderPerMinute, int MaxMessagesPerSenderPerDay,
    int FailedAuthBanThreshold, int FailedAuthBanMinutes)
{
    public string? Validate() =>
        AdminRequestValidation.Range(MaxConnectionsPerIpPerMinute, 0, 100_000, "MaxConnectionsPerIpPerMinute")
        ?? AdminRequestValidation.Range(MaxMessagesPerSenderPerMinute, 0, 100_000, "MaxMessagesPerSenderPerMinute")
        ?? AdminRequestValidation.Range(MaxMessagesPerSenderPerDay, 0, 10_000_000, "MaxMessagesPerSenderPerDay")
        ?? AdminRequestValidation.Range(FailedAuthBanThreshold, 0, 1_000, "FailedAuthBanThreshold")
        ?? AdminRequestValidation.Range(FailedAuthBanMinutes, 0, 525_600, "FailedAuthBanMinutes");

    public RateLimitSettings ToEntity() => new()
    {
        MaxConnectionsPerIpPerMinute = MaxConnectionsPerIpPerMinute,
        MaxMessagesPerSenderPerMinute = MaxMessagesPerSenderPerMinute,
        MaxMessagesPerSenderPerDay = MaxMessagesPerSenderPerDay,
        FailedAuthBanThreshold = FailedAuthBanThreshold,
        FailedAuthBanMinutes = FailedAuthBanMinutes,
    };
}

/// <summary>Header rewrite rule. <c>Action</c>: Set, Append or Remove.</summary>
public sealed record HeaderRewriteRuleRequest(string HeaderName, string Action = "Set", string? MatchValue = null, string? NewValue = null, int SortOrder = 0, bool IsEnabled = true)
{
    private static readonly string[] Actions = ["Set", "Append", "Remove"];

    public string? Validate() =>
        AdminRequestValidation.Required(HeaderName, "HeaderName")
        ?? (HeaderName.Trim().All(c => c > 32 && c < 127 && c != ':') ? null : "HeaderName must be a plain header field name.")
        ?? (Actions.Contains(Action, StringComparer.OrdinalIgnoreCase) ? null : "Action must be Set, Append or Remove.")
        ?? AdminRequestValidation.NoLineBreaks(MatchValue, "MatchValue")
        ?? AdminRequestValidation.NoLineBreaks(NewValue, "NewValue")
        ?? (!Action.Equals("Remove", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(NewValue)
            ? "NewValue is required for Set and Append." : null);

    public HeaderRewriteEntry ToEntity() => new()
    {
        HeaderName = HeaderName.Trim(),
        Action = Actions.First(a => a.Equals(Action, StringComparison.OrdinalIgnoreCase)),
        MatchValue = MatchValue,
        NewValue = NewValue,
        SortOrder = SortOrder,
        IsEnabled = IsEnabled,
    };
}

/// <summary>Sender rewrite rule: envelope senders matching <c>FromPattern</c> are rewritten to <c>ToAddress</c>.</summary>
public sealed record SenderRewriteRuleRequest(string FromPattern, string ToAddress, int SortOrder = 0, bool IsEnabled = true)
{
    public string? Validate() =>
        AdminRequestValidation.Required(FromPattern, "FromPattern")
        ?? AdminRequestValidation.NoLineBreaks(FromPattern, "FromPattern")
        ?? AdminRequestValidation.Address(ToAddress, "ToAddress");

    public SenderRewriteEntry ToEntity() => new() { FromPattern = FromPattern.Trim(), ToAddress = ToAddress.Trim(), SortOrder = SortOrder, IsEnabled = IsEnabled };
}

/// <summary>Shared field checks for the admin API request bodies.</summary>
public static class AdminRequestValidation
{
    public static string? Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? $"{field} is required." : MaxLength(value, 255, field) ?? NoLineBreaks(value, field);

    public static string? MaxLength(string? value, int max, string field) =>
        value is not null && value.Length > max ? $"{field} must not be longer than {max} characters." : null;

    public static string? NoLineBreaks(string? value, string field) =>
        value is not null && value.Any(char.IsControl) ? $"{field} must not contain control characters." : null;

    public static string? Port(int port, string field) =>
        port is >= 1 and <= 65535 ? null : $"{field} must be between 1 and 65535.";

    public static string? Range(int value, int min, int max, string field) =>
        value >= min && value <= max ? null : $"{field} must be between {min} and {max}.";

    /// <summary>An address or CIDR network, parsed by the same Core parser the relay uses for sending IPs.</summary>
    public static string? Network(string? value) =>
        string.IsNullOrWhiteSpace(value) || !IpAddressScope.TryParseAddressOrNetwork(value, out _)
            ? $"Network '{value}' is not an IP address or CIDR network (e.g. 192.168.1.10/32)."
            : null;

    /// <summary>A DNS host/domain name (IDN accepted and checked in its ASCII form) or an IP address.</summary>
    public static string? HostName(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            return $"{field} is required.";
        var host = value.Trim().TrimEnd('.');
        if (IPAddress.TryParse(host, out _))
            return null;
        string ascii;
        try
        {
            ascii = new IdnMapping().GetAscii(host);
        }
        catch (ArgumentException)
        {
            return $"{field} '{value}' is not a valid domain name.";
        }
        return ascii.Length <= 253 && Uri.CheckHostName(ascii) == UriHostNameType.Dns
            ? null
            : $"{field} '{value}' is not a valid domain name.";
    }

    public static string? Selector(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 63
        && value.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? null
            : "Selector must consist of letters, digits, '-', '_' or '.' (at most 63 characters).";

    public static string? Address(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            return $"{field} is required.";
        var at = value.Trim().LastIndexOf('@');
        return at > 0 && at < value.Trim().Length - 1 && NoLineBreaks(value, field) is null
               && HostName(value.Trim()[(at + 1)..], field) is null
            ? null
            : $"{field} '{value}' is not a valid email address.";
    }

    public static string? RetryIntervals(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "RetryIntervalsMinutes is required (e.g. \"1,5,30,120\").";
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length is > 0 and <= 50 && parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is > 0 and <= 10_080)
            ? null
            : "RetryIntervalsMinutes must be a comma-separated list of minutes (1-10080), e.g. \"1,5,30,120\".";
    }
}
