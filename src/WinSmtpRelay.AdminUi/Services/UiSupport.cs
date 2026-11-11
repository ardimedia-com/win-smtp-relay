using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace WinSmtpRelay.AdminUi.Services;

/// <summary>Texts shown to the operator when an action fails. Never show raw exception text — log it instead.</summary>
public static class UiMessages
{
    /// <summary>Shown for an unexpected exception; the detail goes to the log.</summary>
    public const string Unexpected = "Something went wrong. The details were written to the server log.";

    /// <summary>Shown when a circuit event reaches a handler the user's role does not allow.</summary>
    public const string Forbidden = "You don't have permission to do that.";

    /// <summary>Shown when the browser refuses clipboard access (plain HTTP, denied permission).</summary>
    public const string CopyFailed = "Copy failed — select the text and copy it manually.";
}

/// <summary>Helpers shared by the admin pages.</summary>
public static class UiSupportExtensions
{
    /// <summary>
    /// Server-side re-check of a policy inside an event handler. An <c>AuthorizeView</c> only hides the control;
    /// a circuit event can still reach the handler, so every mutating handler re-checks before acting.
    /// </summary>
    public static async Task<bool> IsAllowedAsync(
        this IAuthorizationService authorization, Task<AuthenticationState>? authState, string policy)
    {
        if (authState is null)
            return false;
        var user = (await authState).User;
        return (await authorization.AuthorizeAsync(user, policy)).Succeeded;
    }

    /// <summary>
    /// Copies <paramref name="text"/> to the clipboard. Returns false instead of throwing when the browser refuses
    /// (the async clipboard API exists only in secure contexts; the JS helper falls back to a hidden textarea) or
    /// the circuit is gone.
    /// </summary>
    public static async Task<bool> CopyToClipboardAsync(this IJSRuntime js, string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        try
        {
            return await js.InvokeAsync<bool>("winSmtpRelay.copyText", text);
        }
        catch (JSException) { return false; }
        catch (JSDisconnectedException) { return false; }
        catch (TaskCanceledException) { return false; }
        catch (InvalidOperationException) { return false; } // interop not available (prerender)
    }
}
