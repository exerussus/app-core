using System;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Exerussus.AppCore.Localization
{
    /// <summary>
    /// Биндер текстов вью (страницы, попапа, фрагмента, скрина). Заводится при монтировании и
    /// только при включённой локализации; иначе вью отдаёт <see cref="LocBinder.Passthrough"/>.
    /// </summary>
    internal sealed class ViewText
    {
        private LocBinder _binder;

        public LocBinder Binder => _binder ?? LocBinder.Passthrough;

        /// <summary>Обходит дерево вью. Колбэк — смена языка для контроллера.</summary>
        public void Attach(VisualElement root, Object context, Action onLanguageChanged)
        {
            if (root == null || !Loc.Enabled) return;

            if (_binder == null)
            {
                _binder = new LocBinder(context);
                if (onLanguageChanged != null) _binder.LanguageChanged += onLanguageChanged;
            }

            _binder.BindTree(root);
        }

        public void Detach()
        {
            _binder?.Dispose();
            _binder = null;
        }
    }
}
