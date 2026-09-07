using System;
using System.Collections;
using System.IO;
using System.Text;
using NUnit.Framework;
using SDFUI.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SDFUI.Tests
{
    /// <summary>Actual GPU rendering in a disposable scene, including uGUI clipping and compositing.</summary>
    public sealed class SdfRenderingTests
    {
        private const int Resolution = 128;
        private string folder;
        private string sourcePath;
        private Scene previewScene;
        private Camera camera;
        private Canvas canvas;
        private RenderTexture target;
        private SdfSprite sprite;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("GPU rendering is unavailable on the Null graphics device.");

            folder = "Assets/SDFImageRenderTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            sprite = null;
            sourcePath = CreateGreenSquareSource();
            SdfTextureSettings settings = SdfTextureSettings.Get(sourcePath);
            settings.enabled = true;
            settings.maxSize = 512;
            settings.padding = 16;
            settings.range = 16;
            SdfTextureSettings.Set(sourcePath, settings);
            double deadline = EditorApplication.timeSinceStartup + 30;
            while (!sprite && EditorApplication.timeSinceStartup < deadline)
            {
                foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(sourcePath))
                    if (asset is SdfSprite embedded && embedded.IsValid)
                        sprite = embedded;
                if (!sprite)
                    yield return null;
            }
            Assert.That(sprite, Is.Not.Null, "Render fixture auto-bake did not finish: " + SdfBakeQueue.GetStatus(sourcePath));

            previewScene = EditorSceneManager.NewPreviewScene();
            var cameraObject = NewObject("SDF Render Test Camera", typeof(Camera));
            camera = cameraObject.GetComponent<Camera>();
            camera.enabled = false;
            camera.scene = previewScene;
            camera.overrideSceneCullingMask = EditorSceneManager.GetSceneCullingMask(previewScene);
            camera.transform.position = new Vector3(0, 0, -10);
            camera.orthographic = true;
            camera.orthographicSize = Resolution * 0.5f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 100;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;
            camera.allowHDR = false;
            camera.allowMSAA = false;

            target = new RenderTexture(Resolution, Resolution, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                name = "SDF Test Render",
                antiAliasing = 1,
                hideFlags = HideFlags.HideAndDontSave
            };
            target.Create();
            camera.targetTexture = target;

            var canvasObject = NewObject("SDF Render Test Canvas", typeof(RectTransform), typeof(Canvas));
            canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = camera;
            canvas.referencePixelsPerUnit = 100;
            ((RectTransform)canvas.transform).sizeDelta = new Vector2(Resolution, Resolution);
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(sourcePath))
                SdfBakeQueue.Cancel(sourcePath);
            if (camera)
                camera.targetTexture = null;
            if (target)
            {
                target.Release();
                Object.DestroyImmediate(target);
            }
            if (previewScene.IsValid())
                EditorSceneManager.ClosePreviewScene(previewScene);
            if (!string.IsNullOrEmpty(folder))
                AssetDatabase.DeleteAsset(folder);
        }

        [Test]
        public void OuterAndInnerOutlines_AppearOnTheirRespectiveSidesOfTheSourceEdge()
        {
            SdfImage image = CreateImage(canvas.transform);
            image.OutlineWidth = 6;
            image.OutlineColor = Color.red;
            image.OutlinePosition = SdfOutlinePosition.Outer;
            Color[] outer = Render();
            AssertGreen(Average(outer, -6, -6, 12, 12), "Outer outline preserves the green center.");
            AssertRed(Average(outer, 18, -6, 3, 12), "Outer outline must render beyond the source's x=16 edge.");
            AssertGreen(Average(outer, 11, -6, 3, 12), "Outer outline does not paint inside the source.");

            image.OutlinePosition = SdfOutlinePosition.Inner;
            Color[] inner = Render();
            AssertGreen(Average(inner, -6, -6, 12, 12), "Inner outline preserves the center.");
            AssertRed(Average(inner, 11, -6, 3, 12), "Inner outline paints inside the source edge.");
            Assert.That(Average(inner, 18, -6, 3, 12).a, Is.LessThan(0.05f),
                "Inner outline must not leave a colored exterior ring.");
        }

        [TestCase(SdfOutlinePosition.Outer)]
        [TestCase(SdfOutlinePosition.Center)]
        public void WhiteOutline_WithSourceAntialiasingDifferentFromSdfCoverage_HasNoDarkJoin(SdfOutlinePosition position)
        {
            PrepareWhiteAntialiasedSource();
            SdfImage image = CreateImage(canvas.transform);
            image.OutlineWidth = 6;
            image.OutlineColor = Color.white;
            image.OutlinePosition = position;

            camera.backgroundColor = Color.black;
            Color[] onBlack = Render();
            Color rightJoin = Average(onBlack, 15, -6, 1, 12);
            Color leftJoin = Average(onBlack, -16, -6, 1, 12);
            Assert.That(Mathf.Min(rightJoin.r, rightJoin.g, rightJoin.b), Is.GreaterThan(0.98f),
                "A white source and white outline must not reveal a dark seam inside their combined coverage.");
            Assert.That(Mathf.Min(leftJoin.r, leftJoin.g, leftJoin.b), Is.GreaterThan(0.98f));

            // A transparent black target exposes composite alpha independently
            // from an opaque background, which would always report alpha=1.
            camera.backgroundColor = Color.clear;
            Color[] transparent = Render();
            Assert.That(Average(transparent, 15, -6, 1, 12).a, Is.GreaterThan(0.98f));
            Assert.That(Average(transparent, -16, -6, 1, 12).a, Is.GreaterThan(0.98f));
            Color interior = Average(transparent, -2, -2, 4, 4);
            Assert.That(interior.a, Is.EqualTo(128f / 255).Within(0.015f),
                "Repairing the source edge must not fill intentional transparency deep inside the artwork.");
            Assert.That(interior.r, Is.EqualTo(128f / 255).Within(0.015f));
        }

        [TestCase(0f, 1f)]
        [TestCase(6f, 0f)]
        public void InactiveWhiteOutline_PreservesTheSourcePartialAlpha(float width, float outlineAlpha)
        {
            PrepareWhiteAntialiasedSource();
            SdfImage image = CreateImage(canvas.transform);
            image.OutlineWidth = 0;
            camera.backgroundColor = Color.clear;
            Color[] baseline = Render();
            Color expectedEdge = Average(baseline, 15, -6, 1, 12);
            Assert.That(expectedEdge.a, Is.InRange(0.3f, 0.6f), "Fixture must contain a partial-alpha edge pixel.");

            image.OutlineWidth = width;
            image.OutlineColor = new Color(1, 1, 1, outlineAlpha);
            Color[] actual = Render();
            Color edge = Average(actual, 15, -6, 1, 12);
            Assert.That(edge.a, Is.EqualTo(expectedEdge.a).Within(0.015f));
            Assert.That(edge.r, Is.EqualTo(expectedEdge.r).Within(0.015f));
            Assert.That(Average(actual, -2, -2, 4, 4).a, Is.EqualTo(128f / 255).Within(0.015f));
        }

        [TestCase(SdfImageType.Simple)]
        [TestCase(SdfImageType.Sliced)]
        public void StretchedWhiteOutline_PreservesTransparencyBeyondTheSourceEdgeFootprint(SdfImageType type)
        {
            PrepareWhiteAntialiasedSource();
            if (type == SdfImageType.Sliced)
                sprite.Initialize(sprite.SourceSprite, sprite.ColorTexture, sprite.DistanceTexture, sprite.SourceSize,
                    new Vector4(4, 4, 4, 4), sprite.Pivot, sprite.PixelsPerUnit, sprite.Padding, sprite.DistanceRange);
            SdfImage image = CreateImage(canvas.transform);
            image.Type = type;
            image.rectTransform.sizeDelta = new Vector2(1024, 64);
            image.OutlineColor = Color.white;
            Color baseline = Average(Render(), -8, -3, 16, 1);
            Assert.That(baseline.a, Is.EqualTo(128f / 255).Within(0.015f),
                "The sample lies in the intentionally translucent center, several source pixels from the contour.");

            image.OutlineWidth = 6;
            Color[] outlined = Render();
            Color interior = Average(outlined, -8, -3, 16, 1);
            Assert.That(interior.a, Is.EqualTo(baseline.a).Within(0.015f),
                "Stretching the horizontal axis must not widen the alpha repair around a horizontal source edge.");
            Assert.That(interior.r, Is.EqualTo(baseline.r).Within(0.015f));
            // The horizontal contour is at local y=16 (Simple) or y=18.67 (Sliced).
            Assert.That(Average(outlined, -8, 21, 16, 1).a, Is.GreaterThan(0.9f),
                "The exterior outline must still render while interior transparency is preserved.");
        }

        [Test]
        public void ShadowOffset_MovesTheVisibleBlueSilhouetteToTheRight()
        {
            SdfImage image = CreateImage(canvas.transform);
            image.OutlineWidth = 0;
            image.ShadowColor = Color.blue;
            image.ShadowOffset = new Vector2(20, 0);
            image.ShadowBlur = 0;
            image.ShadowSpread = 0;

            Color[] pixels = Render();
            AssertGreen(Average(pixels, -6, -6, 12, 12), "Opaque fill must remain above its shadow.");
            AssertBlue(Average(pixels, 26, -6, 6, 12), "A positive X shadow offset must render on the right.");
            Assert.That(Average(pixels, -32, -6, 6, 12).a, Is.LessThan(0.05f),
                "The shadow must not appear at the mirrored offset.");
        }

        [Test]
        public void CanvasGroupAndGraphicAlpha_FadeTheCompletedCompositeOnce()
        {
            var group = canvas.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0.5f;
            SdfImage image = CreateImage(canvas.transform);
            image.color = new Color(1, 1, 1, 0.8f);
            image.OutlineWidth = 6;
            image.OutlineColor = Color.red;
            image.ShadowColor = Color.blue;
            image.ShadowOffset = new Vector2(20, 0);
            image.ShadowBlur = 0;

            Color[] pixels = Render();
            Assert.That(Average(pixels, -6, -6, 12, 12).a, Is.EqualTo(0.4f).Within(0.04f), "Fill alpha");
            Assert.That(Average(pixels, 18, -6, 3, 12).a, Is.EqualTo(0.4f).Within(0.04f),
                "Outline above an overlapping shadow must fade once, without leaking shadow alpha.");
            Assert.That(Average(pixels, 28, -6, 4, 12).a, Is.EqualTo(0.4f).Within(0.04f), "Shadow alpha");
        }

        [Test]
        public void TranslatedRectMask2D_ClipsFillAndEffectsInTheCorrectCoordinates()
        {
            RectTransform mask = CreateMask("Rect Clip", canvas.transform, new Vector2(28, 64),
                new Vector2(-8, 0), false);
            SdfImage image = CreateImage(mask);
            image.rectTransform.anchoredPosition = new Vector2(8, 0);
            image.OutlineWidth = 6;
            image.OutlineColor = Color.red;

            Color[] pixels = Render();
            AssertGreen(Average(pixels, -10, -6, 8, 12), "Fill inside the translated clip rectangle.");
            AssertRed(Average(pixels, -21, -6, 3, 12), "Outline inside the clip rectangle must remain visible.");
            Assert.That(Average(pixels, 10, -6, 3, 12).a, Is.LessThan(0.05f), "Fill outside the right clip boundary.");
            Assert.That(Average(pixels, 18, -6, 3, 12).a, Is.LessThan(0.05f), "Outline outside the right clip boundary.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StencilMasks_ClipTranslatedAndNestedBounds(bool nested)
        {
            RectTransform horizontal = CreateMask("Horizontal Stencil", canvas.transform, new Vector2(28, 64),
                new Vector2(-8, 0), true);
            RectTransform parent = nested
                ? CreateMask("Vertical Stencil", horizontal, new Vector2(128, 20), new Vector2(8, -6), true)
                : horizontal;
            SdfImage image = CreateImage(parent);
            image.rectTransform.anchoredPosition = nested ? new Vector2(0, 6) : new Vector2(8, 0);
            image.OutlineWidth = 6;
            image.OutlineColor = Color.red;

            Color[] pixels = Render();
            AssertGreen(Average(pixels, -10, -10, 8, 8), "Fill inside both stencil masks.");
            AssertRed(Average(pixels, -21, -10, 3, 8), "Outline inside both stencil masks.");
            Assert.That(Average(pixels, 10, -10, 3, 8).a, Is.LessThan(0.05f), "First stencil clips the right side.");
            if (nested)
                Assert.That(Average(pixels, -10, 8, 8, 4).a, Is.LessThan(0.05f), "Second stencil clips the top side.");
            else
                AssertGreen(Average(pixels, -10, 8, 8, 4), "A single horizontal mask preserves the top fill.");
        }

        [Test]
        public void SdfImageAsMask_ClipsToItsSilhouetteAndUpdatesItsStencilMaterials()
        {
            SdfImage maskImage = CreateImage(canvas.transform);
            maskImage.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            var childObject = NewObject("Blue Masked Fill", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            childObject.transform.SetParent(maskImage.transform, false);
            ((RectTransform)childObject.transform).sizeDelta = new Vector2(100, 100);
            childObject.GetComponent<Image>().color = Color.blue;

            Color[] initial = Render();
            AssertBlue(Average(initial, -6, -6, 12, 12), "Child must be visible inside the opaque source silhouette.");
            Assert.That(Average(initial, 18, -6, 3, 12).a, Is.LessThan(0.05f),
                "Transparent source pixels must not write the mask stencil.");

            maskImage.OutlineColor = Color.red;
            maskImage.OutlineWidth = 6;
            Color[] changed = Render();
            AssertBlue(Average(changed, 18, -6, 3, 12),
                "The updated SDF outline must enlarge the mask silhouette, while the mask graphic stays hidden.");
            Assert.That(Average(changed, 28, -6, 3, 12).a, Is.LessThan(0.05f),
                "Child pixels outside the complete SDF silhouette must remain clipped.");
            Assert.That(maskImage.canvasRenderer.popMaterialCount, Is.GreaterThan(0));
            Material popMaterial = maskImage.canvasRenderer.GetPopMaterial(0);
            Assert.That(popMaterial.GetVector("_Outline").x, Is.EqualTo(6),
                "Stencil cleanup must use the same updated silhouette as the draw material.");
            Assert.That(popMaterial.IsKeywordEnabled("UNITY_UI_ALPHACLIP"), Is.True,
                "Stencil cleanup must retain alpha clipping on transparent SDF pixels.");
        }

        [Test]
        public void SharedCanvas_WithBackgroundAndTransformedImages_PreservesEachImagesLocalSampling()
        {
            var background = NewObject("Standard UI Background", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            background.transform.SetParent(canvas.transform, false);
            ((RectTransform)background.transform).sizeDelta = new Vector2(Resolution, Resolution);
            background.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.08f, 1);

            SdfImage translated = CreateImage(canvas.transform);
            translated.name = "Translated SDF Image";
            translated.rectTransform.anchoredPosition = new Vector2(-30, -12);

            var transformedParent = NewObject("Scaled And Rotated Parent", typeof(RectTransform));
            transformedParent.transform.SetParent(canvas.transform, false);
            var parentRectangle = (RectTransform)transformedParent.transform;
            parentRectangle.sizeDelta = new Vector2(64, 64);
            parentRectangle.anchoredPosition = new Vector2(30, 16);
            parentRectangle.localScale = new Vector3(0.8f, 1.1f, 1);
            parentRectangle.localRotation = Quaternion.Euler(0, 0, 15);
            SdfImage transformed = CreateImage(parentRectangle);
            transformed.name = "Transformed SDF Image";

            foreach (SdfImage image in new[] { translated, transformed })
            {
                image.rectTransform.sizeDelta = new Vector2(48, 48);
                image.OutlineWidth = 4;
                image.OutlineColor = Color.red;
                image.ShadowColor = Color.blue;
                image.ShadowOffset = new Vector2(10, 0);
                image.ShadowBlur = 0;
            }

            // Canvas batching can transform POSITION into Canvas space. Artwork
            // sampling must still use each Graphic's own local coordinates.
            Color[] pixels = Render();
            foreach (SdfImage image in new[] { translated, transformed })
            {
                AssertGreen(SampleLocal(pixels, image.rectTransform, Vector2.zero),
                    image.name + " fill must stay centered after Canvas batching and transforms.");
                AssertRed(SampleLocal(pixels, image.rectTransform, new Vector2(14, 0)),
                    image.name + " outline must remain at the source contour after transforms.");
                AssertBlue(SampleLocal(pixels, image.rectTransform, new Vector2(19, 0)),
                    image.name + " shadow offset must follow the transformed Graphic's local axes.");
            }
        }

        private GameObject NewObject(string name, params Type[] components)
        {
            var result = new GameObject(name, components) { hideFlags = HideFlags.HideAndDontSave };
            SceneManager.MoveGameObjectToScene(result, previewScene);
            return result;
        }

        private void PrepareWhiteAntialiasedSource()
        {
            // The last inside texel is only 60% covered but still exceeds the
            // baker's 50% threshold, so the existing binary SDF remains correct.
            // This isolates the mismatch between RGBA alpha and SDF coverage.
            Color32[] pixels = sprite.ColorTexture.GetPixels32();
            int width = sprite.ColorTexture.width;
            for (int i = 0; i < pixels.Length; i++)
            {
                int x = i % width - sprite.Padding;
                int y = i / width - sprite.Padding;
                byte alpha = pixels[i].a;
                if ((x == 8 || x == 23) && y >= 8 && y < 24)
                    alpha = 153;
                if (x >= 14 && x < 18 && y >= 14 && y < 18)
                    alpha = 128;
                pixels[i] = new Color32(255, 255, 255, alpha);
            }
            sprite.ColorTexture.SetPixels32(pixels);
            sprite.ColorTexture.Apply(false, false);
        }

        private SdfImage CreateImage(Transform parent)
        {
            var imageObject = NewObject("SDF Render Image", typeof(RectTransform), typeof(CanvasRenderer), typeof(SdfImage));
            imageObject.transform.SetParent(parent, false);
            var image = imageObject.GetComponent<SdfImage>();
            image.rectTransform.sizeDelta = new Vector2(64, 64);
            image.Sprite = sprite;
            image.OutlineWidth = 0;
            image.ShadowColor = Color.clear;
            image.ShadowBlur = 0;
            return image;
        }

        private RectTransform CreateMask(string name, Transform parent, Vector2 size, Vector2 position, bool stencil)
        {
            var maskObject = NewObject(name, typeof(RectTransform));
            maskObject.transform.SetParent(parent, false);
            var rectangle = (RectTransform)maskObject.transform;
            rectangle.sizeDelta = size;
            rectangle.anchoredPosition = position;
            if (stencil)
            {
                maskObject.AddComponent<Image>();
                maskObject.AddComponent<Mask>().showMaskGraphic = false;
            }
            else
            {
                maskObject.AddComponent<RectMask2D>();
            }
            return rectangle;
        }

        private Color[] Render()
        {
            Canvas.ForceUpdateCanvases();
            if (GraphicsSettings.currentRenderPipeline == null)
            {
                camera.Render();
            }
            else
            {
                var request = new RenderPipeline.StandardRequest { destination = target };
                Assert.That(RenderPipeline.SupportsRenderRequest(camera, request), Is.True,
                    "The active render pipeline must support an isolated StandardRequest.");
                RenderPipeline.SubmitRenderRequest(camera, request);
            }

            var previous = RenderTexture.active;
            var readback = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false, true);
            try
            {
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, Resolution, Resolution), 0, 0);
                readback.Apply();
                Shader shader = Resources.Load<Shader>("SDFImage");
                Assert.That(shader, Is.Not.Null);
                Assert.That(shader.isSupported, Is.True);
                var errors = new StringBuilder();
                foreach (var message in ShaderUtil.GetShaderMessages(shader))
                    if (message.severity.ToString() == "Error")
                        errors.AppendLine(message.message);
                Assert.That(errors.Length, Is.Zero, errors.ToString());
                return readback.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(readback);
            }
        }

        private string CreateGreenSquareSource()
        {
            string path = folder + "/GreenSquare.png";
            var source = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color32[32 * 32];
                for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                    pixels[y * 32 + x] = new Color32(0, 255, 0,
                        x >= 8 && x < 24 && y >= 8 && y < 24 ? (byte)255 : (byte)0);
                source.SetPixels32(pixels);
                source.Apply();
                File.WriteAllBytes(path, source.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(source);
            }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 100;
            importer.mipmapEnabled = false;
            importer.isReadable = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            return path;
        }

        private static Color Average(Color[] pixels, int x, int y, int width, int height)
        {
            Color sum = Color.clear;
            for (int row = y + Resolution / 2; row < y + Resolution / 2 + height; row++)
            for (int column = x + Resolution / 2; column < x + Resolution / 2 + width; column++)
                sum += pixels[row * Resolution + column];
            return sum / (width * height);
        }

        private Color SampleLocal(Color[] pixels, RectTransform rectangle, Vector2 local)
        {
            Vector3 viewport = camera.WorldToViewportPoint(rectangle.TransformPoint(local));
            int x = Mathf.Clamp(Mathf.FloorToInt(viewport.x * Resolution), 0, Resolution - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(viewport.y * Resolution), 0, Resolution - 1);
            return pixels[y * Resolution + x];
        }

        private static void AssertGreen(Color color, string context)
        {
            Assert.That(color.g, Is.GreaterThan(0.8f), context);
            Assert.That(color.r + color.b, Is.LessThan(0.15f), context);
            Assert.That(color.a, Is.GreaterThan(0.9f), context);
        }

        private static void AssertRed(Color color, string context)
        {
            Assert.That(color.r, Is.GreaterThan(0.8f), context);
            Assert.That(color.g + color.b, Is.LessThan(0.15f), context);
            Assert.That(color.a, Is.GreaterThan(0.9f), context);
        }

        private static void AssertBlue(Color color, string context)
        {
            Assert.That(color.b, Is.GreaterThan(0.8f), context);
            Assert.That(color.r + color.g, Is.LessThan(0.15f), context);
            Assert.That(color.a, Is.GreaterThan(0.9f), context);
        }
    }
}
