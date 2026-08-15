using UnityEngine;

namespace Exerussus.AppCore.Layout
{
    /// <summary>
    /// Сужает область камеры до полосы кадра, посчитанной <see cref="ScreenMetrics"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Компонент НЕобязателен. Чёрные поля рисует UI (<see cref="AppRunner"/> держит их поверх
    /// всего), поэтому без него картинка уже корректна — камера просто рендерит и то, что скрыто
    /// полями. Этот компонент — чистая экономия филла: не рисуем то, что всё равно закрыто.
    /// </para>
    /// <para>
    /// Вторая камера-подложка не нужна и не предусмотрена. Она была бы нужна, только если бы поля
    /// оставались никем не записанными: за пределами <c>camera.rect</c> содержимое таргета
    /// не определено. Но панель App — screen-space overlay, она рендерится на весь экран
    /// независимо от области камеры, и её элементы закрашивают поля каждый кадр.
    /// </para>
    /// <para>
    /// Своего опроса <c>Screen</c> здесь нет — только сравнение версии метрик, которые считает
    /// <see cref="AppRunner"/>. Второго поллера в проекте не появляется.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class ScreenFrameCamera : MonoBehaviour
    {
        [Tooltip("Камера, область которой сужается. Пусто — берётся с этого же объекта.")]
        [SerializeField] private Camera targetCamera;

        private static readonly Rect FullRect = new Rect(0f, 0f, 1f, 1f);

        private int _appliedVersion = -1;
        private float _appliedShare = -1f;

        private void OnEnable()
        {
            if (targetCamera == null) targetCamera = GetComponent<Camera>();

            // Версию сбрасываем, а не доверяем прошлой: за время выключения метрики могли
            // не поменяться, но rect камеры мы на выходе вернули в полный.
            _appliedVersion = -1;
            _appliedShare = -1f;
            Apply();
        }

        private void OnDisable()
        {
            if (targetCamera != null) targetCamera.rect = FullRect;
            _appliedShare = -1f;
        }

        private void LateUpdate()
        {
            Apply();
        }

        private void Apply()
        {
            if (targetCamera == null) return;
            if (!ScreenMetrics.HasValue) return;
            if (ScreenMetrics.Version == _appliedVersion) return;

            _appliedVersion = ScreenMetrics.Version;

            var share = ScreenMetrics.Share;
            if (Mathf.Approximately(share, _appliedShare)) return;
            _appliedShare = share;

            targetCamera.rect = share >= 1f
                ? FullRect
                : new Rect((1f - share) * 0.5f, 0f, share, 1f);
        }
    }
}
