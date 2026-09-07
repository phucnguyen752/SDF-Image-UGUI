using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SDFUI.Editor
{
    /// <summary>Publishes already-computed data into the source image's import result. Never computes SDF here.</summary>
    public sealed class SdfSourceImporter : AssetPostprocessor
    {
        public override uint GetVersion() => 3;

        private void OnPostprocessSprites(Texture2D texture, Sprite[] sprites)
        {
            if (context == null || !(assetImporter is TextureImporter importer) ||
                !assetPath.StartsWith("Assets/", StringComparison.Ordinal)) return;
            context.DependsOnCustomDependency(SdfBakeCache.DependencyName(assetPath));
            var settings = SdfTextureSettings.Get(importer);
            string fingerprint = settings.enabled ? SdfTextureSettings.Fingerprint(assetPath, importer) : string.Empty;
            foreach (var source in sprites)
            {
                SdfBakeData data = default;
                bool current = settings.enabled && SdfBakeCache.TryRead(assetPath, source, fingerprint, out data);
                // Keep a completed old result while an update is pending or auto-generation is disabled.
                if (!current && !SdfBakeCache.TryReadLatest(assetPath, source, out data)) continue;
                var color = new Texture2D(data.width + data.padding * 2, data.height + data.padding * 2,
                    TextureFormat.RGBA32, false, !data.sRGB)
                {
                    name = source.name + " SDF Color",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideInHierarchy
                };
                color.SetPixels32(data.color);
                color.Apply(false, false);
                var distance = new Texture2D(color.width, color.height, TextureFormat.RHalf, false, true)
                {
                    name = source.name + " SDF Distance",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideInHierarchy
                };
                distance.SetPixelData(data.distanceHalf, 0);
                distance.Apply(false, false);
                var descriptor = ScriptableObject.CreateInstance<SdfSprite>();
                descriptor.name = source.name + " SDF";
                descriptor.hideFlags = HideFlags.HideInHierarchy;
                descriptor.Initialize(source, color, distance, new Vector2Int(data.width, data.height), data.border,
                    data.pivot, data.ppu, data.padding, data.range, data.alphaThreshold, data.fingerprint);
                string identifier = "sdf-image/" + SdfBakeCache.SpriteKey(source);
                context.AddObjectToAsset(identifier + "/color", color);
                context.AddObjectToAsset(identifier + "/distance", distance);
                context.AddObjectToAsset(identifier + "/sprite", descriptor);
                if (!source.AddScriptableObject(descriptor))
                    throw new InvalidOperationException("Could not attach SDF data to Sprite '" + source.name + "'.");
            }
        }

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            foreach (string path in imported)
                if (path.StartsWith("Assets/", StringComparison.Ordinal) && AssetImporter.GetAtPath(path) is TextureImporter &&
                    (SdfTextureSettings.Get(path).enabled || Directory.Exists(Path.Combine(SdfBakeCache.CacheDirectory, AssetDatabase.AssetPathToGUID(path)))))
                    SdfAutoBakeService.Schedule(path);
            foreach (string path in deleted) SdfBakeQueue.Cancel(path);
            foreach (string path in movedFrom) SdfBakeQueue.Cancel(path);
        }

        public static void RefreshTarget(SdfAutoBake binding) => SdfAutoBakeService.ScheduleBinding(binding);
        public static void RefreshTarget(SdfImage image) => SdfAutoBakeService.ScheduleImage(image);
    }

    [InitializeOnLoad]
    internal static class SdfAutoBakeService
    {
        private static readonly HashSet<string> pendingSources = new HashSet<string>();
        private static readonly HashSet<string> publishSources = new HashSet<string>();
        private static readonly HashSet<SdfImage> pendingImages = new HashSet<SdfImage>();
        private static readonly HashSet<SdfAutoBake> pendingBindings = new HashSet<SdfAutoBake>();
        private static Queue<string> restoreDirectories;

        static SdfAutoBakeService()
        {
            SdfImage.ChangedEditor += ScheduleImage;
            SdfAutoBake.Changed += ScheduleBinding;
            SdfBakeQueue.Completed += path => publishSources.Add(path);
            EditorApplication.update += Update;
            EditorApplication.delayCall += RestoreEnabledSources;
            Undo.undoRedoPerformed += RestoreEnabledSources;
        }

        internal static void Schedule(string path) => pendingSources.Add(path);
        internal static void ScheduleImage(SdfImage image)
        {
            if (image) pendingImages.Add(image);
        }

        internal static void ScheduleBinding(SdfAutoBake binding)
        {
            if (binding) pendingBindings.Add(binding);
        }

        private static void RestoreEnabledSources()
        {
            foreach (var image in Resources.FindObjectsOfTypeAll<SdfImage>())
                ScheduleImage(image);
            foreach (var binding in Resources.FindObjectsOfTypeAll<SdfAutoBake>())
                ScheduleBinding(binding);
        }

        private static void Update()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            if (restoreDirectories == null)
                restoreDirectories = new Queue<string>(Directory.Exists(SdfBakeCache.CacheDirectory)
                    ? Directory.GetDirectories(SdfBakeCache.CacheDirectory) : Array.Empty<string>());
            if (restoreDirectories.Count > 0)
            {
                string guid = Path.GetFileName(restoreDirectories.Dequeue());
                if (Guid.TryParseExact(guid, "N", out _))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (AssetImporter.GetAtPath(path) is TextureImporter) pendingSources.Add(path);
                }
            }
            if (publishSources.Count > 0)
            {
                // Publish one source per Editor update to leave room for input and repaint.
                string path = null;
                foreach (string source in publishSources) { path = source; break; }
                publishSources.Remove(path);
                if (AssetImporter.GetAtPath(path) is TextureImporter)
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                pendingSources.Add(path);
            }
            if (pendingSources.Count > 0)
            {
                var paths = new List<string>(pendingSources);
                pendingSources.Clear();
                var images = Resources.FindObjectsOfTypeAll<SdfImage>();
                var bindings = Resources.FindObjectsOfTypeAll<SdfAutoBake>();
                foreach (string path in paths)
                {
                    if (!(AssetImporter.GetAtPath(path) is TextureImporter)) { SdfBakeQueue.Cancel(path); continue; }
                    if (SdfBakeCache.RegisterPublishedDependency(path)) publishSources.Add(path);
                    if (SdfTextureSettings.Get(path).enabled) SdfBakeQueue.Enqueue(path);
                    else SdfBakeQueue.Cancel(path);
                    foreach (var binding in bindings)
                        if (binding.Source && AssetDatabase.GetAssetPath(binding.Source) == path)
                            pendingBindings.Add(binding);
                    foreach (var image in images)
                        if (image.SourceSprite && AssetDatabase.GetAssetPath(image.SourceSprite) == path)
                            pendingImages.Add(image);
                }
            }
            if (pendingBindings.Count > 0)
            {
                var bindings = new List<SdfAutoBake>(pendingBindings);
                pendingBindings.Clear();
                foreach (var binding in bindings)
                {
                    if (!binding || !binding.Target || EditorUtility.IsPersistent(binding)) continue;
                    // Older scenes may still carry the separate source binding component.
                    var target = binding.Target;
                    var previousSource = target.sprite;
                    binding.Resolve();
                    if (target.sprite != previousSource)
                    {
                        EditorUtility.SetDirty(target);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(target);
                    }
                    pendingImages.Add(target);
                }
            }
            if (pendingImages.Count == 0) return;
            var refresh = new List<SdfImage>(pendingImages);
            pendingImages.Clear();
            foreach (var image in refresh)
            {
                if (!image) continue;
                image.RefreshSdf();
                if (!image.SourceSprite) continue;
                string path = AssetDatabase.GetAssetPath(image.SourceSprite);
                // Assigning an ordinary Sprite is valid and does not opt it into generation.
                if (SdfTextureSettings.Get(path).enabled) SdfBakeQueue.Enqueue(path);
            }
            EditorApplication.QueuePlayerLoopUpdate();
        }
    }
}
