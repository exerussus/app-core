using UnityEngine;
#if APPDECK_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Опрос клавиатуры и мыши для AppDeck — без состояния, поверх той системы ввода, что включена
    /// в проекте (новый Input System или старый Input Manager). Читает устройства напрямую:
    /// клавиша открытия должна работать, даже когда фокус у чужого окна UI или игра забрала ввод.
    /// </summary>
    internal static class DeckInput
    {
        public static bool WasPressed(DeckHotkey hotkey)
        {
            if (hotkey == DeckHotkey.None) return false;

#if APPDECK_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            return keyboard != null && keyboard[ToKey(hotkey)].wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(ToKeyCode(hotkey));
#else
            return false;
#endif
        }

        public static bool EscapePressed
        {
            get
            {
#if APPDECK_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
                Keyboard keyboard = Keyboard.current;
                return keyboard != null && keyboard.escapeKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
                return Input.GetKeyDown(KeyCode.Escape);
#else
                return false;
#endif
            }
        }

        public static bool ShiftHeld
        {
            get
            {
#if APPDECK_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
                Keyboard keyboard = Keyboard.current;
                return keyboard != null && keyboard.shiftKey.isPressed;
#elif ENABLE_LEGACY_INPUT_MANAGER
                return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
#else
                return false;
#endif
            }
        }

        /// <summary>Левая кнопка мыши нажата в этом кадре. Позиция — пиксели экрана, начало слева снизу.</summary>
        public static bool LeftClick(out Vector2 screenPosition)
        {
#if APPDECK_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                screenPosition = mouse.position.ReadValue();
                return true;
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetMouseButtonDown(0))
            {
                screenPosition = Input.mousePosition;
                return true;
            }
#endif
            screenPosition = default;
            return false;
        }

#if APPDECK_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        private static Key ToKey(DeckHotkey hotkey) => hotkey switch
        {
            DeckHotkey.BackQuote => Key.Backquote,
            DeckHotkey.F1 => Key.F1,
            DeckHotkey.F2 => Key.F2,
            DeckHotkey.F3 => Key.F3,
            DeckHotkey.F4 => Key.F4,
            DeckHotkey.F5 => Key.F5,
            DeckHotkey.F6 => Key.F6,
            DeckHotkey.F7 => Key.F7,
            DeckHotkey.F8 => Key.F8,
            DeckHotkey.F9 => Key.F9,
            DeckHotkey.F10 => Key.F10,
            DeckHotkey.F11 => Key.F11,
            DeckHotkey.F12 => Key.F12,
            DeckHotkey.Insert => Key.Insert,
            DeckHotkey.Home => Key.Home,
            DeckHotkey.PageUp => Key.PageUp,
            DeckHotkey.Backslash => Key.Backslash,
            _ => Key.None,
        };
#endif

        /// <summary>Та же клавиша в терминах событий UI Toolkit — чтобы глотать её символ в поле ввода.</summary>
        public static KeyCode ToKeyCode(DeckHotkey hotkey) => hotkey switch
        {
            DeckHotkey.BackQuote => KeyCode.BackQuote,
            DeckHotkey.F1 => KeyCode.F1,
            DeckHotkey.F2 => KeyCode.F2,
            DeckHotkey.F3 => KeyCode.F3,
            DeckHotkey.F4 => KeyCode.F4,
            DeckHotkey.F5 => KeyCode.F5,
            DeckHotkey.F6 => KeyCode.F6,
            DeckHotkey.F7 => KeyCode.F7,
            DeckHotkey.F8 => KeyCode.F8,
            DeckHotkey.F9 => KeyCode.F9,
            DeckHotkey.F10 => KeyCode.F10,
            DeckHotkey.F11 => KeyCode.F11,
            DeckHotkey.F12 => KeyCode.F12,
            DeckHotkey.Insert => KeyCode.Insert,
            DeckHotkey.Home => KeyCode.Home,
            DeckHotkey.PageUp => KeyCode.PageUp,
            DeckHotkey.Backslash => KeyCode.Backslash,
            _ => KeyCode.None,
        };
    }
}
