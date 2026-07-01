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
