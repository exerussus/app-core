// AppCore Analytics: видимость страницы WebGL. Android WebView в фоне и закрытие вкладки не всегда дают
// blur/quitting — visibilitychange и pagehide дают. Подписка одна на экземпляр Unity; снимается на Quit
// (Module.deinitializers и явный вызов из C#), чтобы замыкания не держали кучу старого экземпляра.
mergeInto(LibraryManager.library, {
    AppAnalytics_HookVisibility: function (callback) {
        if (typeof document === 'undefined' || typeof window === 'undefined') return;
        if (Module.__appAnalyticsVisibility) return;

        var send = function (visible) {
            try {
                {{{ makeDynCall('vi', 'callback') }}}(visible ? 1 : 0);
            } catch (e) {
                // сбой wasm не прятать: в консоль, но не наружу в обработчик браузера
                if (typeof console !== 'undefined' && console.error) console.error('[AppAnalytics] visibility callback failed', e);
            }
        };

        var onVisibility = function () { send(document.visibilityState !== 'hidden'); };
        var onPageHide = function () { send(false); };
        document.addEventListener('visibilitychange', onVisibility);
        window.addEventListener('pagehide', onPageHide);

        var unhook = function () {
            var h = Module.__appAnalyticsVisibility;
            if (!h) return;
            Module.__appAnalyticsVisibility = null;
            document.removeEventListener('visibilitychange', h.visibility);
            window.removeEventListener('pagehide', h.pagehide);
        };

        Module.__appAnalyticsVisibility = { visibility: onVisibility, pagehide: onPageHide, unhook: unhook };
        if (Module.deinitializers && typeof Module.deinitializers.push === 'function') Module.deinitializers.push(unhook);
    },

    AppAnalytics_UnhookVisibility: function () {
        var h = Module.__appAnalyticsVisibility;
        if (h && h.unhook) h.unhook();
    }
});
