namespace WinSmtpRelay.Core.Mail;

/// <summary>
/// Structured content of an internally-generated email. One definition renders BOTH the plain-text
/// part and the HTML alternative (see <see cref="SystemEmail"/> / <see cref="SystemEmailHtml"/>), so
/// the two can never drift apart. Render order: <see cref="Paragraphs"/>, the action button,
/// <see cref="MonospaceBlock"/>, <see cref="ClosingParagraphs"/>, <see cref="FooterNote"/>.
/// </summary>
public sealed record SystemEmailContent
{
    /// <summary>Card heading in the HTML part (usually a shorter form of the subject).</summary>
    public required string Title { get; init; }

    /// <summary>Body paragraphs shown before the button/monospace block.</summary>
    public required IReadOnlyList<string> Paragraphs { get; init; }

    /// <summary>CTA button label; the button and its plain-link fallback are omitted when null.</summary>
    public string? ButtonText { get; init; }

    /// <summary>Raw action URL for the button (HTML-encoded by the renderer).</summary>
    public string? ActionUrl { get; init; }

    /// <summary>Preformatted block (stats, diagnostics) rendered monospace; omitted when null.</summary>
    public string? MonospaceBlock { get; init; }

    /// <summary>Paragraphs shown after the monospace block (e.g. guidance below diagnostics).</summary>
    public IReadOnlyList<string> ClosingParagraphs { get; init; } = [];

    /// <summary>Muted footer note ("If you did not request this …"); omitted when null.</summary>
    public string? FooterNote { get; init; }
}
