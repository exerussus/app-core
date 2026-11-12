using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Буфер обмена для AppDeck. Редактор, десктоп, мобилки — <see cref="GUIUtility.systemCopyBuffer"/>;
    /// WebGL — navigator.clipboard через Plugins/WebGL/AppDeckClipboard.jslib (там systemCopyBuffer
    /// живёт только внутри плеера и в системный буфер не попадает).
    /// </summary>
    internal static class DeckClipboard
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern int AppDeck_CopyText(string text);
#endif

        /// <summary>Положить текст в буфер обмена. Только главный поток. false — платформа отказала.</summary>
        public static bool Copy(string text)
        {
            text ??= string.Empty;

#if UNITY_WEBGL && !UNITY_EDITOR
            // внутренний буфер тоже заполняем: вставка внутри самого плеера (TextField) берёт его
            GUIUtility.systemCopyBuffer = text;
            try
            {
                return AppDeck_CopyText(text) != 0;
            }
            catch
            {
                return false;
            }
#else
            GUIUtility.systemCopyBuffer = text;
            return true;
#endif
        }
    }
}
