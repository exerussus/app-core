using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Views
{
    /// <summary>
    /// Реестр хостов и фрагментов одного вью плюс правило переключения.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Вынесено из <see cref="AppPage"/> и <see cref="AppPopup"/> в отдельный объект, а не в общий
    /// базовый класс: базового класса у них нет, и заводить его ради переиспользования сотни
    /// строк — лишняя связность. Оба держат это приватным полем и пробрасывают наружу два метода.
    /// </para>
    /// <para>
    /// На параллельные переключения одного хоста не рассчитан: <see cref="Show"/> ждёт
    /// <c>OnDeactivate</c> прежнего фрагмента, и два одновременных вызова переплетутся.
    /// Переключать фрагменты хоста должен один владелец — как навигацией страниц рулит
    /// только навигатор.
    /// </para>
    /// </remarks>
    public sealed class FragmentSlots
    {
        private readonly Dictionary<long, AppFragment> _fragments = new();
        private readonly List<FragmentHost> _hosts = new();

        // Что сейчас показано в каждом хосте. Ключ — индекс хоста в _hosts.
        private readonly Dictionary<int, AppFragment> _shown = new();

        private AppRunner _runner;

        public bool IsEmpty => _fragments.Count == 0;

        /// <summary>
        /// Собирает фрагменты вью. Вызывается в PreInitialize, до построения вёрстки:
        /// <c>includeInactive</c> обязателен — объекты страниц к этому моменту уже погашены.
        /// </summary>
        public void CollectFragments(Component owner)
        {
            _fragments.Clear();

            var found = owner.GetComponentsInChildren<AppFragment>(true);
            for (var i = 0; i < found.Length; i++)
            {
                var fragment = found[i];
                fragment.PreInitialize();

                if (fragment.FragmentUid.IsEmpty())
                {
                    Debug.LogError($"[AppCore] Фрагмент '{fragment.name}' без id — пропущен.");
                    continue;
                }

                if (_fragments.TryGetValue(fragment.FragmentUid.Id, out var clash))
                {
                    Debug.LogError(
                        $"[AppCore] Дублирующийся id фрагмента \"{fragment.FragmentIdRaw}\": " +
                        $"объекты '{clash.name}' и '{fragment.name}'.");
                    continue;
                }

                _fragments.Add(fragment.FragmentUid.Id, fragment);
            }
        }

        /// <summary>Кэширует хосты вью. Вызывается один раз при монтировании вёрстки.</summary>
        public void CollectHosts(VisualElement root, AppRunner runner)
        {
            _runner = runner;
            _hosts.Clear();
            _shown.Clear();

            if (root == null) return;
            root.Query<FragmentHost>().ForEach(host => _hosts.Add(host));
        }

        /// <summary>
        /// Разворачивает фрагмент в его хосте. Прежнее содержимое хоста скрывается.
        /// </summary>
        public async UniTask Show(FragmentId fragmentId, string hostId)
        {
            if (!_fragments.TryGetValue(fragmentId.Id, out var fragment))
            {
                Debug.LogError($"[AppCore] Фрагмент {fragmentId} не найден.");
                return;
            }

            // Явно переданный хост важнее того, что записан на самом фрагменте.
            var index = ResolveHost(hostId ?? fragment.HostIdRaw);
            if (index < 0) return;

            if (_shown.TryGetValue(index, out var current))
            {
                if (ReferenceEquals(current, fragment))
                {
                    await fragment.Show();   // no-op, если уже показан
                    return;
                }

                await current.Hide();
            }

            _shown[index] = fragment;

            // Тот же фрагмент попросили в другой хост: Mount увидел бы готовый Root и молча
            // оставил вёрстку на прежнем месте. Сносим и разворачиваем заново.
            if (fragment.Root != null && fragment.Root.parent != _hosts[index]) fragment.Unmount();

            // RegisterAppView — только на фактическом монтировании: иначе на фрагменте с
            // unmountOnHide манипуляторы копились бы с каждым показом.
            if (fragment.Mount(_hosts[index])) _runner?.RegisterAppView(fragment);

            await fragment.Show();
        }

        /// <summary>Скрывает то, что сейчас показано в хосте.</summary>
        public async UniTask Hide(string hostId)
        {
            var index = ResolveHost(hostId);
            if (index < 0) return;

            if (!_shown.TryGetValue(index, out var current)) return;

            _shown.Remove(index);
            await current.Hide();
        }

        /// <summary>Что сейчас показано в хосте. <c>null</c> — ничего или хост не найден.</summary>
        public AppFragment GetShown(string hostId)
        {
            var index = ResolveHost(hostId);
            return index >= 0 && _shown.TryGetValue(index, out var current) ? current : null;
        }

        private int ResolveHost(string hostId)
        {
            if (_hosts.Count == 0)
            {
                Debug.LogError("[AppCore] В вёрстке вью нет ни одного FragmentHost.");
                return -1;
            }

            // Пустой id — единственный хост вью. Если их несколько, выбор неоднозначен.
            if (string.IsNullOrEmpty(hostId))
            {
                if (_hosts.Count == 1) return 0;

                Debug.LogError("[AppCore] Хостов несколько — нужен host-id, дефолтного выбора нет.");
                return -1;
            }

            for (var i = 0; i < _hosts.Count; i++)
                if (string.Equals(_hosts[i].HostId, hostId, System.StringComparison.Ordinal)) return i;

            Debug.LogError($"[AppCore] FragmentHost с host-id \"{hostId}\" во вью не найден.");
            return -1;
        }
    }
}
