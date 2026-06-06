window.themeInterop = {
    toggleDarkMode: function (isDark) {
        document.documentElement.classList.toggle('dark', isDark);
        localStorage.setItem('winsmtprelay-theme', isDark ? 'dark' : 'light');
    },
    setColorTheme: function (theme) {
        if (theme) {
            document.documentElement.setAttribute('data-theme', theme);
        } else {
            document.documentElement.removeAttribute('data-theme');
        }
        localStorage.setItem('winsmtprelay-color-theme', theme || '');
        document.documentElement.dispatchEvent(new CustomEvent('bb-theme-changed'));
    },
    isDarkMode: function () {
        return document.documentElement.classList.contains('dark');
    },
    getColorTheme: function () {
        return localStorage.getItem('winsmtprelay-color-theme') || '';
    }
};

// Apply the per-browser stored theme (dark mode + colour theme) to <html>. Mirrors the inline
// re-applier in App.razor's <head> (which runs on a full document load before first paint).
window.applyStoredTheme = function () {
    try {
        var mode = localStorage.getItem('winsmtprelay-theme');
        document.documentElement.classList.toggle('dark', mode === 'dark');
        var theme = localStorage.getItem('winsmtprelay-color-theme');
        if (theme) {
            document.documentElement.setAttribute('data-theme', theme);
        } else {
            document.documentElement.removeAttribute('data-theme');
        }
    } catch (e) {
        /* localStorage unavailable */
    }
};

// Blazor enhanced navigation — and the enhanced form post behind the tenant switcher — patches the DOM
// to match the server response. The server HTML carries no theme attributes (they are applied client
// side), so the patch strips the dark class / data-theme and the theme is lost (e.g. when switching
// tenants). Re-apply the stored theme after every enhanced load so the per-browser theme survives.
(function registerEnhancedLoad() {
    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', window.applyStoredTheme);
    } else {
        setTimeout(registerEnhancedLoad, 50);
    }
})();
