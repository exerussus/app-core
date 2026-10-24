// AppCore Analytics: видимость страницы WebGL. Android WebView в фоне и закрытие вкладки не всегда дают
// blur/quitting — visibilitychange и pagehide дают. Подписка одна на страницу; ошибки не выпускаются.
mergeInto(LibraryManager.library, {
    AppAnalytics_HookVisibility: function (callback) {
        if (typeof document === 'undefined' || typeof window === 'undefined') return;
        if (Module.__appAnalyticsVisibilityHooked) return;
        Module.__appAnalyticsVisibilityHooked = true;

        var send = function (visible) {
            try {
                {{{ makeDynCall('vi', 'callback') }}}(visible ? 1 : 0);
            } catch (e) {
            }
        };

        document.addEventListener('visibilitychange', function () {
            send(document.visibilityState !== 'hidden');
        });
        window.addEventListener('pagehide', function () {
            send(false);
        });
    }
});
