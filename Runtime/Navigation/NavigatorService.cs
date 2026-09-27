using System.Threading;
using Cysharp.Threading.Tasks;
using Exerussus.DI;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Services;
using Exerussus.AppCore.Input;
using Exerussus.AppCore.Views;
using Cursor = UnityEngine.Cursor;

namespace Exerussus.AppCore.Navigation
{
    /// <summary>
    /// Декларативная навигация: кнопки с классами <c>.to-*__navigation</c> / <c>.open-*__navigation</c>,
    /// кнопка «назад» и настройки курсора страницы.
    /// </summary>
    /// <remarks>
    /// Поведение «назад» и курсора — поля <see cref="AppPage"/> (<see cref="AppPage.BackAction"/>,
    /// <see cref="AppPage.CursorMode"/>), а не служебные элементы вёрстки.
    /// </remarks>
    public class NavigatorService : IAppService, IAppManipulatorBuilder
    {
        [Inject] private readonly AppRunner _appRunner;

        public void OnInject(DependenciesContainer container)
        {
            if (container.Has<InputAdapter>())
            {
                var adapter = container.Get<InputAdapter>();
                adapter.OnBackPressed += OnBackPressed;
            }
        }

        public UniTask Initialize(CancellationToken token)
        {
            _appRunner.OnPageChanged += OnPageChanged;
            return UniTask.CompletedTask;
        }

        /// <summary>Аппаратная кнопка «назад» (Android, Escape).</summary>
        /// <remarks>
        /// Страница без настроенного «назад» ничего не делает: иначе системная кнопка начала бы
        /// выкидывать пользователя с корневых страниц. Попап при этом закрывается всегда — он
        /// поверх всего и перехватывает жест первым.
        /// </remarks>
        private void OnBackPressed() => Back(fallbackToPrevPage: false);

        /// <summary>
        /// Единая семантика «назад» для аппаратной кнопки и для кнопки в вёрстке.
        /// </summary>
        /// <param name="fallbackToPrevPage">
        /// Что делать, когда у страницы «назад» не настроен. Для кнопки в вёрстке — <c>true</c>:
        /// само её наличие и есть намерение. Для аппаратной кнопки — <c>false</c>.
        /// </param>
        private void Back(bool fallbackToPrevPage)
        {
            // 1. Верхний попап перехватывает «назад» раньше страницы.
            if (_appRunner.IsActiveAnyPopup())
            {
                _appRunner.CloseActivePopup();
                return;
            }

            var page = _appRunner.CurrentPage;
            if (page == null) return;

            // 2. Настройка страницы: конкретная цель или возврат по стеку.
            switch (page.BackAction)
            {
                case PageBackAction.Previous:
                    _appRunner.SwitchToPrevPage();
                    return;

                case PageBackAction.ToPage when !page.BackPageUid.IsEmpty():
                    _appRunner.SwitchToPage(page.BackPageUid);
                    return;
            }

            // 3. Не настроено — решает вызывающая сторона.
            if (fallbackToPrevPage) _appRunner.SwitchToPrevPage();
        }

        public void OnBuildButtonManipulator(IAppView appView, Button button)
        {
            if (button.ClassListContains(NavigationLink.BackClass))
            {
                button.clicked += () => Back(fallbackToPrevPage: true);
                return;
            }

            foreach (var (className, pageUid) in NavigationLink.LinksPages)
            {
                if (!button.ClassListContains(className)) continue;

                var target = pageUid;
                button.clicked += () => _appRunner.SwitchToPage(target);
                return;
            }

            foreach (var (className, popupUid) in NavigationLink.LinksPopups)
            {
                if (!button.ClassListContains(className)) continue;

                var target = popupUid;
                button.clicked += () => _appRunner.OpenPopup(target);
                return;
            }
        }

        private void OnPageChanged((PageId prev, PageId current) ctx)
        {
            var page = _appRunner.CurrentPage;
            if (page == null) return;

            switch (page.CursorMode)
            {
                case PageCursorMode.Lock:
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                    break;

                case PageCursorMode.Unlock:
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                    break;
            }
        }
    }
}
