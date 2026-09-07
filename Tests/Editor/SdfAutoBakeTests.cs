using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using SDFUI.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SDFUI.Tests
{
    public sealed class SdfAutoBakeTests
    {
        private string folder;
        private string sourcePath;
        private int completedCount;
        private int importedCount;

        [SetUp]
        public void SetUp()
        {
            folder = "Assets/SDFImageAutoTest_" + Guid.NewGuid().ToString("N");
            sourcePath = folder + "/Source.png";
            completedCount = 0;
            importedCount = 0;
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            SdfBakeQueue.Completed += OnCompleted;
            SdfAutoBakeImportCounter.Imported += OnImported;
        }

        [TearDown]
        public void TearDown()
        {
            SdfBakeQueue.Completed -= OnCompleted;
            SdfAutoBakeImportCounter.Imported -= OnImported;
            if (!string.IsNullOrEmpty(sourcePath))
                SdfBakeQueue.Cancel(sourcePath);
            if (!string.IsNullOrEmpty(folder))
                AssetDatabase.DeleteAsset(folder);
        }

        [UnityTest]
        public IEnumerator OrdinarySpriteImport_IsDisabledByDefaultAndDoesNotScheduleSdfWork()
        {
            CreateSource();
            SdfTextureSettings settings = SdfTextureSettings.Get(sourcePath);
            Assert.That(settings.enabled, Is.False);
            Assert.That(settings.maxSize, Is.EqualTo(512));
            int importsAfterSetup = importedCount;
            yield return Settle();

            Assert.That(FindEmbedded(), Is.Null);
            Assert.That(completedCount, Is.Zero);
            Assert.That(importedCount, Is.EqualTo(importsAfterSetup), "An ordinary import must not start an import loop.");
            Assert.That(Directory.GetFiles(folder, "*.asset"), Is.Empty);
        }

        [UnityTest]
        public IEnumerator EnablingAndRebaking_EmbedsAllResultsInSourceWithStableObjectIds()
        {
            CreateSource();
            byte[] originalPng = File.ReadAllBytes(sourcePath);
            string importerBefore = ImporterSettingsJson();
            string sourceSpriteId = StableId(AssetDatabase.LoadAssetAtPath<Sprite>(sourcePath));
            Enable(8);
            yield return WaitForBake(1, result => result.Padding == 8);

            SdfSprite first = FindEmbedded();
            string mainId = StableId(first);
            string colorId = StableId(first.ColorTexture);
            string distanceId = StableId(first.DistanceTexture);
            AssertSharedSourcePath(first);

            Enable(12);
            yield return WaitForBake(2, result => result.Padding == 12);
            SdfSprite second = FindEmbedded();
            AssertSharedSourcePath(second);
            Assert.That(StableId(second), Is.EqualTo(mainId));
            Assert.That(StableId(second.ColorTexture), Is.EqualTo(colorId));
            Assert.That(StableId(second.DistanceTexture), Is.EqualTo(distanceId));
            Assert.That(StableId(AssetDatabase.LoadAssetAtPath<Sprite>(sourcePath)), Is.EqualTo(sourceSpriteId));
            Assert.That(second.ColorTexture.width, Is.EqualTo(56));
            Assert.That(second.DistanceTexture.width, Is.EqualTo(56));
            Assert.That(File.ReadAllBytes(sourcePath), Is.EqualTo(originalPng));
            Assert.That(ImporterSettingsJson(), Is.EqualTo(importerBefore), "SDF opt-in must preserve source texture settings.");
            Assert.That(Directory.GetFiles(folder, "*.asset"), Is.Empty, "No separate baked asset should be written.");
        }

        [UnityTest]
        public IEnumerator DisablingBeforeQueueCompletion_CancelsPendingWorkAndPreventsManualEnqueue()
        {
            CreateSource();
            Enable(8);
            SdfTextureSettings disabled = SdfTextureSettings.Get(sourcePath);
            disabled.enabled = false;
            SdfTextureSettings.Set(sourcePath, disabled);
            SdfBakeQueue.Enqueue(sourcePath);
            yield return Settle();

            Assert.That(SdfTextureSettings.Get(sourcePath).enabled, Is.False);
            Assert.That(FindEmbedded(), Is.Null);
            Assert.That(completedCount, Is.Zero, "Canceled or disabled sources must not publish completed results.");
        }

        [UnityTest]
        public IEnumerator Default512Bake_CanCancelWhileActiveAndKeepsTheEditorUpdating()
        {
            CreateSource(512);
            SdfTextureSettings settings = SdfTextureSettings.Get(sourcePath);
            Assert.That(settings.maxSize, Is.EqualTo(512));
            settings.enabled = true;
            SdfTextureSettings.Set(sourcePath, settings);
            Assert.That(completedCount, Is.Zero, "Opting in must return before the asynchronous bake completes.");

            double startDeadline = EditorApplication.timeSinceStartup + 10;
            while (!SdfBakeQueue.GetStatus(sourcePath).StartsWith("Baking ", StringComparison.Ordinal)
                && EditorApplication.timeSinceStartup < startDeadline)
                yield return null;
            Assert.That(SdfBakeQueue.GetStatus(sourcePath), Does.StartWith("Baking "),
                "The test must cancel an active GPU/worker job, not only an item still queued.");

            var cancelTimer = System.Diagnostics.Stopwatch.StartNew();
            SdfBakeQueue.Cancel(sourcePath);
            cancelTimer.Stop();
            yield return Settle();
            Assert.That(SdfTextureSettings.Get(sourcePath).enabled, Is.True);
            Assert.That(completedCount, Is.Zero, "An explicit cancellation must not automatically restart the same active job.");
            Assert.That(FindEmbedded(), Is.Null);

            int editorUpdates = 0;
            double previousUpdate = EditorApplication.timeSinceStartup;
            double maximumUpdateGap = 0;
            void CountEditorUpdate()
            {
                double now = EditorApplication.timeSinceStartup;
                maximumUpdateGap = Math.Max(maximumUpdateGap, now - previousUpdate);
                previousUpdate = now;
                editorUpdates++;
            }

            EditorApplication.update += CountEditorUpdate;
            var bakeTimer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                settings.enabled = true;
                SdfTextureSettings.Set(sourcePath, settings);
                Assert.That(completedCount, Is.Zero);
                yield return WaitForBake(1, result => result.SourceSize == new Vector2Int(512, 512));
                bakeTimer.Stop();
                Assert.That(editorUpdates, Is.GreaterThanOrEqualTo(2),
                    "The Editor must continue processing updates while the default-size bake runs.");
                Assert.That(FindEmbedded().ColorTexture.width, Is.EqualTo(576));
                TestContext.Out.WriteLine(FormattableString.Invariant(
                    $"SDF512_TIMING total_ms={bakeTimer.Elapsed.TotalMilliseconds:F1} max_update_gap_ms={maximumUpdateGap * 1000:F1} editor_updates={editorUpdates} cancel_call_ms={cancelTimer.Elapsed.TotalMilliseconds:F1}"));
            }
            finally
            {
                EditorApplication.update -= CountEditorUpdate;
            }
        }

        [UnityTest]
        public IEnumerator RapidSettingsChanges_PublishOnlyTheNewestRequestedResult()
        {
            CreateSource();
            Enable(8);
            Enable(12);
            Enable(20, 0.65f);
            yield return WaitForBake(1, result => result.Padding == 20 && Mathf.Approximately(result.AlphaThreshold, 0.65f));
            int completionsAfterLatest = completedCount;
            yield return Settle();

            SdfSprite result = FindEmbedded();
            Assert.That(result.Padding, Is.EqualTo(20));
            Assert.That(result.DistanceRange, Is.EqualTo(20));
            Assert.That(result.AlphaThreshold, Is.EqualTo(0.65f).Within(0.0001f));
            Assert.That(completedCount, Is.EqualTo(completionsAfterLatest), "Superseded jobs must not publish later.");
            Assert.That(completedCount, Is.EqualTo(1), "Changes queued before the next Editor update should coalesce.");
        }

        [UnityTest]
        public IEnumerator MaximumSize_BoundsGeneratedTexturesWithoutChangingNativeDisplaySize()
        {
            CreateSource(128);
            Sprite source = AssetDatabase.LoadAssetAtPath<Sprite>(sourcePath);
            float originalNativeWidth = source.rect.width / source.pixelsPerUnit;
            string originalTextureId = StableId(source.texture);
            string originalMainId = StableId(AssetDatabase.LoadMainAssetAtPath(sourcePath));
            SdfTextureSettings settings = SdfTextureSettings.Get(sourcePath);
            settings.enabled = true;
            settings.maxSize = 64;
            settings.padding = 8;
            settings.range = 8;
            SdfTextureSettings.Set(sourcePath, settings);
            yield return WaitForBake(1, result => result.SourceSize.x == 64);

            SdfSprite result = FindEmbedded();
            Assert.That(result.SourceSize, Is.EqualTo(new Vector2Int(64, 64)));
            Assert.That(result.ColorTexture.width, Is.EqualTo(80));
            Assert.That(result.DistanceTexture.width, Is.EqualTo(80));
            Assert.That(result.SourceSize.x / result.PixelsPerUnit, Is.EqualTo(originalNativeWidth).Within(0.0001f));
            Texture2D original = AssetDatabase.LoadAssetAtPath<Sprite>(sourcePath).texture;
            Assert.That(original.width, Is.EqualTo(128), "Maximum SDF size must not downsample the original imported texture.");
            Assert.That(StableId(original), Is.EqualTo(originalTextureId));
            Assert.That(StableId(AssetDatabase.LoadMainAssetAtPath(sourcePath)), Is.EqualTo(originalMainId),
                "Attaching generated subassets must not replace the source's original main asset.");
        }

        [UnityTest]
        public IEnumerator EditingSourcePixels_RebakesAutomaticallyWithoutChangingOriginalRgbaOrLoopingImports()
        {
            CreateSource();
            Enable(8);
            yield return WaitForBake(1, result => result.ColorTexture.GetPixel(24, 24).a > 0.95f);
            string sourceSpriteId = StableId(AssetDatabase.LoadAssetAtPath<Sprite>(sourcePath));
            string sourceTextureId = StableId(AssetDatabase.LoadAssetAtPath<Sprite>(sourcePath).texture);

            WriteSourcePixels(true);
            byte[] editedPng = File.ReadAllBytes(sourcePath);
            AssetDatabase.ImportAsset(sourcePath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            yield return WaitForBake(2, result => result.ColorTexture.GetPixel(24, 24).a < 0.05f
                && result.ColorTexture.GetPixel(13, 24).r > 0.95f);

            SdfSprite edited = FindEmbedded();
            Assert.That(edited.ColorTexture.GetPixel(13, 18).a, Is.GreaterThan(0.95f),
                "The source's lower opaque strip must remain at the bottom after GPU readback.");
            Assert.That(edited.ColorTexture.GetPixel(13, 30).a, Is.LessThan(0.05f),
                "The asymmetric top strip must remain transparent after GPU readback.");

            Assert.That(File.ReadAllBytes(sourcePath), Is.EqualTo(editedPng));
            var importedSprite = AssetDatabase.LoadAssetAtPath<Sprite>(sourcePath);
            Texture2D originalTexture = importedSprite.texture;
            Assert.That(originalTexture, Is.Not.Null);
            Assert.That(originalTexture.width, Is.EqualTo(32));
            Assert.That(StableId(originalTexture), Is.EqualTo(sourceTextureId), "The original Sprite must keep its original RGBA texture.");
            Assert.That(AssetDatabase.GetAssetPath(originalTexture), Is.EqualTo(sourcePath));
            Assert.That(StableId(importedSprite), Is.EqualTo(sourceSpriteId));
            Assert.That(((TextureImporter)AssetImporter.GetAtPath(sourcePath)).isReadable, Is.False);

            int importsAfterCompletion = importedCount;
            int completionsAfterCompletion = completedCount;
            yield return Settle();
            Assert.That(importedCount, Is.EqualTo(importsAfterCompletion), "Embedding generated objects must not reimport repeatedly.");
            Assert.That(completedCount, Is.EqualTo(completionsAfterCompletion));
        }

        [UnityTest]
        public IEnumerator MultipleSprites_KeepSeparateArtworkMetadataAndStableAttachmentsAfterReimport()
        {
            CreateSpriteSheet();
            Enable(8);
            yield return WaitForBake(1, result => result.Padding == 8);

            Sprite left = FindSourceSprite("Left Green");
            Sprite right = FindSourceSprite("Right Red");
            SdfSprite leftData = SdfSprite.FromSprite(left);
            SdfSprite rightData = SdfSprite.FromSprite(right);
            Assert.That(leftData, Is.Not.Null);
            Assert.That(rightData, Is.Not.Null);
            Assert.That(leftData, Is.Not.EqualTo(rightData));
            Assert.That(leftData.SourceSprite, Is.EqualTo(left));
            Assert.That(rightData.SourceSprite, Is.EqualTo(right));
            Assert.That(left.rect, Is.EqualTo(new Rect(0, 0, 32, 32)));
            Assert.That(right.rect, Is.EqualTo(new Rect(32, 0, 32, 32)));
            Assert.That(leftData.SourceSize, Is.EqualTo(new Vector2Int(32, 32)));
            Assert.That(rightData.SourceSize, Is.EqualTo(new Vector2Int(32, 32)));
            Assert.That(Vector2.Distance(leftData.Pivot, new Vector2(0.25f, 0.75f)), Is.LessThan(0.0001f));
            Assert.That(Vector2.Distance(rightData.Pivot, new Vector2(0.8f, 0.2f)), Is.LessThan(0.0001f));
            Assert.That(leftData.Border, Is.EqualTo(new Vector4(2, 4, 6, 8)));
            Assert.That(rightData.Border, Is.EqualTo(new Vector4(5, 3, 7, 9)));
            Assert.That(leftData.PixelsPerUnit, Is.EqualTo(64));
            Assert.That(rightData.PixelsPerUnit, Is.EqualTo(64));

            Color leftCenter = leftData.ColorTexture.GetPixel(24, 24);
            Color rightCenter = rightData.ColorTexture.GetPixel(24, 24);
            Assert.That(leftCenter.g, Is.GreaterThan(0.95f));
            Assert.That(leftCenter.r, Is.LessThan(0.05f));
            Assert.That(rightCenter.r, Is.GreaterThan(0.95f));
            Assert.That(rightCenter.g, Is.LessThan(0.05f));
            Assert.That(leftData.ColorTexture.GetPixel(12, 10).a, Is.GreaterThan(0.95f));
            Assert.That(rightData.ColorTexture.GetPixel(12, 10).a, Is.LessThan(0.05f),
                "The second Sprite must use its own cropped alpha shape, not the first Sprite's rectangle.");

            string[] originalIds = SpriteAndDataIds(left, leftData, right, rightData);
            foreach (Object item in new Object[] { leftData, rightData, leftData.ColorTexture,
                rightData.ColorTexture, leftData.DistanceTexture, rightData.DistanceTexture })
                Assert.That(AssetDatabase.GetAssetPath(item), Is.EqualTo(sourcePath));

            AssetDatabase.ImportAsset(sourcePath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            yield return Settle();
            left = FindSourceSprite("Left Green");
            right = FindSourceSprite("Right Red");
            leftData = SdfSprite.FromSprite(left);
            rightData = SdfSprite.FromSprite(right);
            Assert.That(leftData, Is.Not.Null);
            Assert.That(rightData, Is.Not.Null);
            Assert.That(SpriteAndDataIds(left, leftData, right, rightData), Is.EqualTo(originalIds));
            Assert.That(completedCount, Is.EqualTo(1), "A force import with unchanged inputs should reuse the completed sheet cache.");
            Assert.That(Directory.GetFiles(folder, "*.asset"), Is.Empty);
        }

        private void Enable(int padding, float threshold = 0.5f)
        {
            SdfTextureSettings settings = SdfTextureSettings.Get(sourcePath);
            settings.enabled = true;
            settings.maxSize = 512;
            settings.padding = padding;
            settings.range = padding;
            settings.alphaThreshold = threshold;
            SdfTextureSettings.Set(sourcePath, settings);
        }

        private IEnumerator WaitForBake(int minimumCompletions, Func<SdfSprite, bool> expected)
        {
            double deadline = EditorApplication.timeSinceStartup + 30;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                SdfSprite result = FindEmbedded();
                if (completedCount >= minimumCompletions && result && result.IsValid && expected(result))
                    yield break;
                yield return null;
            }
            Assert.Fail("Auto-bake did not publish the expected result: " + SdfBakeQueue.GetStatus(sourcePath));
        }

        private static IEnumerator Settle()
        {
            double until = EditorApplication.timeSinceStartup + 0.75;
            while (EditorApplication.timeSinceStartup < until)
                yield return null;
        }

        private void CreateSource(int size = 32)
        {
            WriteSourcePixels(false, size);
            AssetDatabase.ImportAsset(sourcePath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(sourcePath);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 64;
            importer.isReadable = false;
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }

        private void CreateSpriteSheet()
        {
            var texture = new Texture2D(64, 32, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color32[64 * 32];
                for (int y = 0; y < 32; y++)
                for (int x = 0; x < 64; x++)
                    pixels[y * 64 + x] = x < 32 ? new Color32(0, 255, 0, 255)
                        : new Color32(255, 0, 0, x >= 40 && x < 56 && y >= 4 && y < 28 ? (byte)255 : (byte)0);
                texture.SetPixels32(pixels);
                texture.Apply();
                File.WriteAllBytes(sourcePath, texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
            AssetDatabase.ImportAsset(sourcePath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(sourcePath);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Multiple;
            importer.spritePixelsPerUnit = 64;
            importer.isReadable = false;
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
#pragma warning disable 618
            importer.spritesheet = new[]
            {
                new SpriteMetaData
                {
                    name = "Left Green", rect = new Rect(0, 0, 32, 32), alignment = (int)SpriteAlignment.Custom,
                    pivot = new Vector2(0.25f, 0.75f), border = new Vector4(2, 4, 6, 8)
                },
                new SpriteMetaData
                {
                    name = "Right Red", rect = new Rect(32, 0, 32, 32), alignment = (int)SpriteAlignment.Custom,
                    pivot = new Vector2(0.8f, 0.2f), border = new Vector4(5, 3, 7, 9)
                }
            };
#pragma warning restore 618
            importer.SaveAndReimport();
        }

        private Sprite FindSourceSprite(string name)
        {
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(sourcePath))
                if (asset is Sprite source && source.name == name)
                    return source;
            Assert.Fail("The source sheet did not import Sprite " + name);
            return null;
        }

        private static string[] SpriteAndDataIds(Sprite left, SdfSprite leftData, Sprite right, SdfSprite rightData)
        {
            return new[]
            {
                StableId(left), StableId(leftData), StableId(leftData.ColorTexture), StableId(leftData.DistanceTexture),
                StableId(right), StableId(rightData), StableId(rightData.ColorTexture), StableId(rightData.DistanceTexture)
            };
        }

        private void WriteSourcePixels(bool edited, int size = 32)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color32[size * size];
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool inside = y >= size / 4 && y < (edited ? size * 5 / 8 : size * 3 / 4)
                        && (edited ? x >= size / 16 && x < size * 3 / 8 : x >= size / 4 && x < size * 3 / 4);
                    pixels[y * size + x] = edited
                        ? new Color32(255, 0, 0, inside ? (byte)255 : (byte)0)
                        : new Color32(0, 255, 0, inside ? (byte)255 : (byte)0);
                }
                texture.SetPixels32(pixels);
                texture.Apply();
                File.WriteAllBytes(sourcePath, texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        private SdfSprite FindEmbedded()
        {
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(sourcePath))
                if (asset is SdfSprite result)
                    return result;
            return null;
        }

        private void AssertSharedSourcePath(SdfSprite result)
        {
            Assert.That(AssetDatabase.GetAssetPath(result), Is.EqualTo(sourcePath));
            Assert.That(AssetDatabase.GetAssetPath(result.ColorTexture), Is.EqualTo(sourcePath));
            Assert.That(AssetDatabase.GetAssetPath(result.DistanceTexture), Is.EqualTo(sourcePath));
            int count = 0;
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(sourcePath))
                if (asset is SdfSprite)
                    count++;
            Assert.That(count, Is.EqualTo(1), "A Single sprite import must expose exactly one embedded SDF.");
            Sprite source = AssetDatabase.LoadAssetAtPath<Sprite>(sourcePath);
            var attached = new ScriptableObject[source.GetScriptableObjectsCount()];
            uint attachedCount = source.GetScriptableObjects(attached);
            Assert.That(attachedCount, Is.GreaterThan(0), "The generated descriptor must be attached to the original Sprite.");
            Assert.That(attached, Does.Contain(result), "Sharing the asset path alone does not establish a Sprite attachment.");
            Assert.That(SdfSprite.FromSprite(source), Is.EqualTo(result), "Runtime resolution must return the attached generated data.");
        }

        private string ImporterSettingsJson()
        {
            var settings = new TextureImporterSettings();
            ((TextureImporter)AssetImporter.GetAtPath(sourcePath)).ReadTextureSettings(settings);
            return JsonUtility.ToJson(settings);
        }

        private static string StableId(Object asset)
        {
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId), Is.True);
            return guid + ":" + localId;
        }

        private void OnCompleted(string path)
        {
            if (path == sourcePath)
                completedCount++;
        }

        private void OnImported(string path)
        {
            if (path == sourcePath)
                importedCount++;
        }
    }

    internal sealed class SdfAutoBakeImportCounter : AssetPostprocessor
    {
        internal static event Action<string> Imported;

        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (Imported == null)
                return;
            foreach (string path in importedAssets)
                Imported(path);
        }
    }
}
