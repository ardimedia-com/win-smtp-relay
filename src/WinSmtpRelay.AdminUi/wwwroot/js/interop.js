// Small interop helpers for the WinSmtpRelay admin UI.
window.winSmtpRelay = window.winSmtpRelay || {};

// The browser's IANA time-zone id (e.g. "Europe/Zurich"), used to render UTC timestamps locally.
window.winSmtpRelay.getTimeZoneName = function () {
    try { return Intl.DateTimeFormat().resolvedOptions().timeZone || ''; }
    catch (e) { return ''; }
};

// Date.getTimezoneOffset(): minutes to add to local time to reach UTC (UTC+2 => -120).
window.winSmtpRelay.getTimeZoneOffsetMinutes = function () {
    return new Date().getTimezoneOffset();
};

// Reset the scrollable content region to the top. The app shell is a fixed viewport height, so the
// window itself does not scroll — the inner .app-main does. On navigation we reset it so each page
// starts at the top (the window-scroll reset the fixed shell used to get for free no longer applies).
window.winSmtpRelay.scrollMainToTop = function () {
    var el = document.querySelector('.app-main');
    if (el) el.scrollTo(0, 0);
};

// Copies text to the clipboard and resolves to true on success. navigator.clipboard exists only in secure
// contexts (HTTPS or localhost); on plain HTTP fall back to a hidden textarea + execCommand('copy').
window.winSmtpRelay.copyText = async function (text) {
    try {
        if (navigator.clipboard && window.isSecureContext) {
            await navigator.clipboard.writeText(text);
            return true;
        }
    } catch (e) { /* fall through to the legacy path */ }
    try {
        var ta = document.createElement('textarea');
        ta.value = text;
        ta.setAttribute('readonly', '');
        ta.style.position = 'fixed';
        ta.style.top = '-1000px';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        var ok = document.execCommand('copy');
        document.body.removeChild(ta);
        return !!ok;
    } catch (e) {
        return false;
    }
};
