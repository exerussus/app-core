// AppDeck: копирование в системный буфер обмена из WebGL. navigator.clipboard — асинхронный и требует
// безопасного контекста; иначе (http, старый браузер, отказ) — запасной путь через textarea + execCommand.
// Клик по кнопке UI Toolkit обрабатывается в следующем кадре, но ещё в окне «пользовательского жеста» браузера.
mergeInto(LibraryManager.library, {
    AppDeck_CopyText: function (textPtr) {
        var text = UTF8ToString(textPtr);

        var fallback = function () {
            try {
                if (typeof document === 'undefined' || !document.body) return false;
                var area = document.createElement('textarea');
                area.value = text;
                area.setAttribute('readonly', '');
                area.style.position = 'fixed';
                area.style.top = '-1000px';
                area.style.opacity = '0';
                document.body.appendChild(area);
                area.select();
                var ok = document.execCommand && document.execCommand('copy');
                document.body.removeChild(area);
                return !!ok;
            } catch (e) {
                return false;
            }
        };

        try {
            if (typeof navigator !== 'undefined' && navigator.clipboard && navigator.clipboard.writeText &&
                (typeof window === 'undefined' || window.isSecureContext !== false)) {
                navigator.clipboard.writeText(text).catch(function () {
                    if (!fallback() && typeof console !== 'undefined' && console.warn)
                        console.warn('[AppDeck] clipboard write refused');
                });
                return 1;
            }
        } catch (e) {
            // ниже — запасной путь
        }

        return fallback() ? 1 : 0;
    }
});
