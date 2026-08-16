using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Audio;

namespace Exerussus.AppCore.Views
{
    /// <summary>
    /// Сменное содержимое внутри вью: одна страница, разный UXML в зависимости от режима,
    /// конфига, состояния игры или выбранной мини-игры.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Фрагмент — полноценный <see cref="IAppView"/>, поэтому вся обвязка достаётся ему даром:
    /// <c>AppRunner.RegisterAppView</c> обходит его кнопки и навешивает звуки, сигналы
    /// и навигацию, а безопасная зона с кадром приходят сверху, с общего контейнера слоёв.
    /// Отдельного механизма здесь нет — есть точка подстановки и правило переключения.
    /// </para>
    /// <para>
    /// Что фрагменту НЕ достаётся: <c>back__action-hook</c> и настройки курсора. Их разбирает
    /// <c>NavigatorService</c> по событию монтирования СТРАНИЦЫ — «назад» это свойство экрана,
    /// а не его внутреннего состояния.
    /// </para>
    /// </remarks>
    public class AppFragment : MonoBehaviour, IAppView
    {
        [Tooltip("Идентификатор фрагмента. По нему вью его и разворачивает.")]
        [SerializeField] private string fragmentId;

        [Tooltip("Идентификатор хоста, в который разворачивать. Пусто — единственный хост вью.")]
        [SerializeField] private string hostId;

        [SerializeField] private VisualTreeAsset visualTree;

        [Tooltip("Своя библиотека звуков. Пусто — берётся общая из AppRunner.")]
        [SerializeField] private UISoundLibrary overrideSoundLibrary;

        [SerializeField] private AppFragmentController controller;

        [Tooltip("Сносить вёрстку при скрытии вместо переключения display. Для тяжёлых фрагментов " +
                 "(мини-игры): освобождает память, но каждый показ — заново Instantiate и обвязка, " +
                 "а состояние вёрстки не переживает скрытие.")]
        [SerializeField] private bool unmountOnHide;

        private bool _hasController;

        public string FragmentIdRaw => fragmentId;
        public string HostIdRaw => hostId;
        public FragmentId FragmentUid { get; private set; }
        public AppFragmentController Controller => controller;
        public bool HasController => _hasController;
        public bool UnmountOnHide => unmountOnHide;
        public UISoundLibrary OverrideSoundLibrary => overrideSoundLibrary;

        /// <summary>
        /// Корневой элемент. Null, пока фрагмент не смонтирован.
        /// Тип — <c>VisualElement</c>, как того требует <see cref="IAppView"/>: C# не допускает
        /// ковариантность в свойствах интерфейса, а фактически здесь лежит TemplateContainer.
        /// </summary>
        public VisualElement Root { get; private set; }

        /// <summary>Показан ли фрагмент прямо сейчас.</summary>
        public bool IsVisible { get; private set; }

        public void PreInitialize()
        {
            _hasController = controller != null;
            FragmentUid = new FragmentId(fragmentId);
        }

        /// <summary>
        /// Разворачивает вёрстку в хост, если она ещё не развёрнута.
        /// </summary>
        /// <returns><c>true</c>, если монтирование произошло именно сейчас — вызывающая сторона
        /// обязана прогнать по фрагменту <c>RegisterAppView</c>.</returns>
        public bool Mount(VisualElement host)
        {
            if (Root != null) return false;

            Root = visualTree.Instantiate();
            Root.name = fragmentId;
            Root.style.flexGrow = 1;
            // Обёртка не должна быть целью пика — по той же причине, что и у страницы:
            // кликабельность решает вёрстка фрагмента.
            Root.pickingMode = PickingMode.Ignore;
            Root.style.display = DisplayStyle.None;
            host.Add(Root);

            if (_hasController)
            {
                controller.Root = Root;
                controller.Initialize();
            }

            return true;
        }

        /// <summary>Показывает уже смонтированный фрагмент.</summary>
        public UniTask Show()
        {
            if (Root == null || IsVisible) return UniTask.CompletedTask;

            IsVisible = true;
            Root.style.display = DisplayStyle.Flex;
            return _hasController ? controller.OnActivate() : UniTask.CompletedTask;
        }

        /// <summary>
        /// Скрывает фрагмент. При <see cref="UnmountOnHide"/> вёрстка сносится, и следующий
        /// показ пройдёт через <see cref="Mount"/> заново.
        /// </summary>
        public async UniTask Hide()
        {
            if (Root == null || !IsVisible) return;

            IsVisible = false;

            // Контроллеру даём отработать ДО сноса: в OnDeactivate он ещё может читать вёрстку.
            if (_hasController) await controller.OnDeactivate();

            if (Root == null) return;   // снесли, пока ждали

            if (unmountOnHide) Unmount();
            else Root.style.display = DisplayStyle.None;
        }

        /// <summary>Сносит вёрстку. Ссылки контроллера на элементы после этого недействительны.</summary>
        public void Unmount()
        {
            if (Root == null) return;

            Root.RemoveFromHierarchy();
            Root = null;
            IsVisible = false;
            if (_hasController) controller.Root = null;
        }
    }
}
