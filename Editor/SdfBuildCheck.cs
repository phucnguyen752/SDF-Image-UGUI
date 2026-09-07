using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SDFUI.Editor
{
    /// <summary>Fails with an actionable message instead of waiting for background jobs during a build.</summary>
    public sealed class SdfBuildCheck : IPreprocessBuildWithReport, IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            var roots = new HashSet<string>(EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path));
            foreach (var asset in PlayerSettings.GetPreloadedAssets())
            {
                string path = AssetDatabase.GetAssetPath(asset);
                if (!string.IsNullOrEmpty(path)) roots.Add(path);
            }
            foreach (string path in AssetDatabase.GetAllAssetPaths())
                if (path.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    path.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) < 0 && !AssetDatabase.IsValidFolder(path)) roots.Add(path);
            foreach (string path in AssetDatabase.GetDependencies(roots.ToArray()))
            {
                if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab) Check(prefab);
            }
        }

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null) return;
            foreach (var root in scene.GetRootGameObjects()) Check(root);
        }

        private static void Check(GameObject root)
        {
            foreach (var image in root.GetComponentsInChildren<SdfImage>(true))
            {
                if (!image.enabled || !image.SourceSprite) continue;
                string path = AssetDatabase.GetAssetPath(image.SourceSprite);
                // Unbaked source sprites intentionally use the normal Unity Image path.
                if (!SdfTextureSettings.Get(path).enabled) continue;
                string fingerprint = SdfTextureSettings.Fingerprint(path);
                var baked = SdfSprite.FromSprite(image.SourceSprite);
                bool ready = baked && baked.IsValid && baked.BakeFingerprint == fingerprint &&
                    SdfBakeCache.TryRead(path, image.SourceSprite, fingerprint, out _);
                if (!ready)
                    throw new BuildFailedException("SDF is not ready for '" + path + "'. Wait for Generate SDF to finish before building, or disable automatic generation.");
            }
        }
    }
}
