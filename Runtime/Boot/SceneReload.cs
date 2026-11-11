using UnityEngine.SceneManagement;

namespace Exerussus.AppCore.Boot
{
    /// <summary>
    /// Перезагрузка активной сцены для перезапуска приложения.
    /// </summary>
    internal static class SceneReload
    {
        /// <summary>
        /// Перезагружает активную сцену. Сцена вне Build Settings (buildIndex &lt; 0) грузится
        /// по пути: в редакторе — через <c>EditorSceneManager.LoadSceneInPlayMode</c>, которому
        /// Build Settings не нужны; в билде такой сцены быть не может, путь — последний шанс.
        /// </summary>
        public static void ReloadActive()
        {
            var scene = SceneManager.GetActiveScene();

            if (scene.buildIndex >= 0)
            {
                SceneManager.LoadScene(scene.buildIndex);
                return;
            }

#if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
                scene.path, new LoadSceneParameters(LoadSceneMode.Single));
#else
            SceneManager.LoadScene(string.IsNullOrEmpty(scene.path) ? scene.name : scene.path);
#endif
        }
    }
}
