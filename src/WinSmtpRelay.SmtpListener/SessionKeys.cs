namespace WinSmtpRelay.SmtpListener;

/// <summary>
/// The keys this relay keeps in <c>ISessionContext.Properties</c>. One place, so the writer and every reader
/// agree on the spelling and on the lifetime of each value.
/// <para>
/// Two lifetimes: SESSION values (authentication, endpoint policy, HELO name) stay until the connection
/// closes; MESSAGE values (owning tenant, recipient count, budgets) belong to one MAIL transaction and are
/// cleared by <see cref="RelayMailboxFilter"/> at every MAIL. The library resets its own transaction on
/// MAIL and RSET, but it does not know about these, and a value that survived into the next message is
/// exactly how a second message used to inherit the first one's tenant.
/// </para>
/// </summary>
internal static class SessionKeys
{
    // ---- Session lifetime ----

    /// <summary>The SMTP username after a successful AUTH (string).</summary>
    public const string AuthenticatedUser = "AuthenticatedUser";

    /// <summary>The tenant of the authenticated SMTP user (int). Set at AUTH; never overwritten by a MAIL.</summary>
    public const string AuthTenantId = "WinSmtpRelay:AuthTenantId";

    /// <summary>Present (true) when the endpoint requires STARTTLS before MAIL.</summary>
    public const string RequireTls = "WinSmtpRelay:RequireTls";

    /// <summary>The name the client gave in HELO/EHLO (string, unsanitized).</summary>
    public const string HeloName = "WinSmtpRelay:HeloName";

    /// <summary>True when the client greeted with EHLO (ESMTP), false for HELO.</summary>
    public const string UsedEhlo = "WinSmtpRelay:UsedEhlo";

    /// <summary>
    /// True while the command line being parsed may be an AUTH continuation (a credential) that reached the
    /// command parser because the AUTH itself was refused. Read by the rejection recorder to redact it.
    /// </summary>
    public const string RedactRejectedLine = "WinSmtpRelay:RedactRejectedLine";

    // ---- Message (transaction) lifetime ----

    /// <summary>The tenant that owns the current message (int). Re-resolved at every MAIL.</summary>
    public const string TenantId = "TenantId";

    /// <summary>Number of recipients accepted for the current message (int).</summary>
    public const string RecipientCount = "WinSmtpRelay:RecipientCount";

    /// <summary>True once a recipient in a hosted or backup-MX domain was accepted for the current message.</summary>
    public const string HasHostedRecipient = "WinSmtpRelay:HasHostedRecipient";

    /// <summary>The authenticated user's record for the current message (<c>RelayUser</c>), for SendAs and budgets.</summary>
    public const string RelayUser = "WinSmtpRelay:RelayUser";

    /// <summary>Every message-lifetime key, cleared at each MAIL.</summary>
    public static readonly string[] MessageScoped = [TenantId, RecipientCount, HasHostedRecipient, RelayUser];
}
