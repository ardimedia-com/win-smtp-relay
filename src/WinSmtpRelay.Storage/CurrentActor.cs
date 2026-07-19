using WinSmtpRelay.Core.Interfaces;

namespace WinSmtpRelay.Storage;

/// <summary>
/// Mutable, scope-lifetime holder for the acting admin — the audit counterpart of
/// <see cref="CurrentTenant"/>. Default state (never set) means no human is acting (background/system),
/// and audit rows written from such a scope carry a null actor.
/// </summary>
public class CurrentActor : ICurrentActor
{
    public int? UserId { get; private set; }
    public int? ApiKeyId { get; private set; }
    public string? Email { get; private set; }

    public void Set(int? userId, string? email, int? apiKeyId = null)
    {
        UserId = userId;
        Email = email;
        ApiKeyId = apiKeyId;
    }
}
