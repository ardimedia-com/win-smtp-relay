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
