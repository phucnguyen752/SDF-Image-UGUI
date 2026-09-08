using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;
using Object = UnityEngine.Object;

namespace SDFUI.Tests
{
    /// <summary>GPU regressions for whole-string effects, including TMP fallback submeshes.</summary>
    public sealed class SdfTextTests
    {
        private const int Resolution = 256;
        private readonly List<TMP_FontAsset> generatedFonts = new List<TMP_FontAsset>();
        private Scene previewScene;
        private Camera camera;
        private Canvas canvas;
        private RenderTexture target;
        private TMP_FontAsset font;

        [OneTimeSetUp]
        public void RequireFontResources()
        {
            if (!Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF"))
                Assert.Ignore("Import TMP Essential Resources before running SdfTextTests.");
        }

        [SetUp]
        public void SetUp()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("GPU rendering is unavailable on the Null graphics device.");

            font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
            Assert.That(font, Is.Not.Null, "Import TMP Essential Resources before running the text render tests.");
            previewScene = EditorSceneManager.NewPreviewScene();
            camera = NewObject("Text Test Camera", typeof(Camera)).GetComponent<Camera>();
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
            target = new RenderTexture(Resolution, Resolution, 24, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear) { hideFlags = HideFlags.HideAndDontSave, antiAliasing = 1 };
            target.Create();
            camera.targetTexture = target;
            canvas = NewObject("Text Test Canvas", typeof(RectTransform), typeof(Canvas)).GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = camera;
            ((RectTransform)canvas.transform).sizeDelta = new Vector2(Resolution, Resolution);
        }

        [TearDown]
        public void TearDown()
        {
            if (camera) camera.targetTexture = null;
            if (target)
            {
                target.Release();
                Object.DestroyImmediate(target);
            }
            if (previewScene.IsValid()) EditorSceneManager.ClosePreviewScene(previewScene);
            foreach (TMP_FontAsset generated in generatedFonts)
            {
                if (generated.material) Object.DestroyImmediate(generated.material);
                foreach (Texture2D atlas in generated.atlasTextures)
                    if (atlas) Object.DestroyImmediate(atlas);
                Object.DestroyImmediate(generated);
            }
            generatedFonts.Clear();
        }

        [Test]
        public void CloselySpacedGlyphs_ThickOutlineNeverPaintsOverAnyGlyphFace()
        {
            SdfText text = CreateText(canvas.transform, "AVAVA");
            Color[] face = Render();
            EnableEffects(text);
            Color[] effects = Render();
            SaveCapture("tmp-close-letters-face.png", face);
            SaveCapture("tmp-close-letters.png", effects);
            AssertFaceUnchanged(face, effects);
            Assert.That(CountExteriorEffect(face, effects), Is.GreaterThan(100),
                "The comparison must include a visible outline, not two face-only renders.");
        }

        [Test]
        public void ThickOutline_FillsTheExteriorStrokeWithoutHolesAtConcaveCorners()
        {
            SdfText text = CreateText(canvas.transform, "LE\nUP");
            text.font = CreateFont("LEVELUP", 256, 48, 2048);
            text.fontSize = 80;
            text.fontStyle = FontStyles.Bold;
            text.characterSpacing = 0;
            text.lineSpacing = -12;
            text.rectTransform.sizeDelta = new Vector2(240, 220);
            Color[] face = Render();
            text.OutlineEnabled = true;
            text.OutlineWidth = 7;
            text.OutlineSoftness = 0.8f;
            text.OutlineColor = Color.cyan;
            Color[] outlined = Render();
            SaveCapture("tmp-thick-outline-face.png", face);
            SaveCapture("tmp-thick-outline.png", outlined);

            // Independently dilate opaque face pixels in Canvas space. The two-pixel
            // margin accommodates antialiasing and softness without using shader math.
            var expectedStroke = new bool[face.Length];
            const int radius = 5;
            for (int y = radius; y < Resolution - radius; y++)
            for (int x = radius; x < Resolution - radius; x++)
            {
                if (face[y * Resolution + x].a < 0.99f) continue;
                for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                    if (dx * dx + dy * dy <= radius * radius)
                        expectedStroke[(y + dy) * Resolution + x + dx] = true;
            }

            int samples = 0, holes = 0;
            float minimumAlpha = 1;
            for (int i = 0; i < face.Length; i++)
            {
                if (!expectedStroke[i] || face[i].a > 0.01f) continue;
                samples++;
                minimumAlpha = Mathf.Min(minimumAlpha, outlined[i].a);
                if (outlined[i].a < 0.95f) holes++;
            }
            Assert.That(samples, Is.GreaterThan(100), "The comparison must cover the exterior outline.");
            Assert.That(holes, Is.Zero,
                "A seven-unit outline left holes within five pixels of an opaque glyph face. Minimum alpha: " + minimumAlpha);
        }

        [Test]
        public void FallbackFontSubmeshes_AllOutlinesRemainBehindAllGlyphFaces()
        {
            SdfText text = CreateText(canvas.transform, "AVAVA");
            TMP_FontAsset primary = CreateFont("A");
            TMP_FontAsset fallback = CreateFont("V");
            primary.fallbackFontAssetTable = new List<TMP_FontAsset> { fallback };
            text.font = primary;
            text.ForceMeshUpdate();
            Assert.That(text.textInfo.materialCount, Is.GreaterThanOrEqualTo(2),
                "The regression must actually draw a separate fallback font material.");
            Color[] face = Render();
            EnableEffects(text);
            Color[] effects = Render();
            SaveCapture("tmp-fallback-letters-face.png", face);
            SaveCapture("tmp-fallback-letters.png", effects);
            AssertFaceUnchanged(face, effects);
            Assert.That(CountExteriorEffect(face, effects), Is.GreaterThan(100));
        }

        [Test]
        public void ChangingTextEmptyingAndDisabling_ClearsEveryEffectLayer()
        {
            SdfText text = CreateText(canvas.transform, "AVAVA");
            EnableEffects(text);
            Assert.That(CountVisible(Render()), Is.GreaterThan(200));
            text.ClearMesh();
            Assert.That(CountVisible(Render()), Is.Zero, "ClearMesh must clear the borrowed effect meshes too.");
            text.text = "AVA";
            Assert.That(CountVisible(Render()), Is.GreaterThan(100));
            text.text = string.Empty;
            Assert.That(CountVisible(Render()), Is.Zero, "Empty text must not retain an old shadow or outline mesh.");
            text.text = "O";
            Assert.That(CountVisible(Render()), Is.GreaterThan(100));
            text.enabled = false;
            Assert.That(CountVisible(Render()), Is.Zero, "Disabling the component must hide its sibling effects.");
            text.enabled = true;
            Assert.That(CountVisible(Render()), Is.GreaterThan(100));
            text.gameObject.SetActive(false);
            Assert.That(CountVisible(Render()), Is.Zero, "Deactivating the source must hide its sibling effects.");
        }

        [Test]
        public void CustomUpdateGeometry_EffectsKeepFollowingTheUploadedFaceMeshAcrossRenderUpdates()
        {
            SdfText text = CreateText(canvas.transform, "O");
            EnableEffects(text);
            Render();
            Mesh replacement = Object.Instantiate(text.canvasRenderer.GetMesh());
            try
            {
                Vector3[] vertices = replacement.vertices;
                for (int i = 0; i < vertices.Length; i++) vertices[i].x += 70;
                replacement.vertices = vertices;
                text.UpdateGeometry(replacement, 0);
                for (int frame = 0; frame < 2; frame++)
                {
                    Color[] pixels = Render();
                    Assert.That(CountVisible(pixels), Is.GreaterThan(100));
                    for (int y = 0; y < Resolution; y++)
                    for (int x = 0; x < Resolution / 2 + 20; x++)
                        Assert.That(pixels[y * Resolution + x].a, Is.LessThan(0.02f),
                            "An effect reverted to the original textInfo mesh after UpdateGeometry.");

                    Vector3[] faceVertices = text.canvasRenderer.GetMesh().vertices;
                    int matched = 0;
                    foreach (SdfTextLayer layer in canvas.GetComponentsInChildren<SdfTextLayer>())
                    {
                        if (layer.Owner != text) continue;
                        // GetMesh exposes a renderer-owned copy, so compare geometry rather than object identity.
                        CollectionAssert.AreEqual(faceVertices, layer.canvasRenderer.GetMesh().vertices);
                        matched++;
                    }
                    Assert.That(matched, Is.EqualTo(2));
                }
            }
            finally
            {
                text.ClearMesh();
                Object.DestroyImmediate(replacement);
            }
        }

        [Test]
        public void TextEnteringRectMaskDuringMeshRebuild_UncullsEffectsWithoutRegisteringAnotherRebuild()
        {
            var mask = (RectTransform)NewObject("Late text clip", typeof(RectTransform),
                typeof(UnityEngine.UI.RectMask2D)).transform;
            mask.SetParent(canvas.transform, false);
            mask.sizeDelta = new Vector2(60, 100);
            SdfText text = CreateText(mask, "O");
            text.alignment = TextAlignmentOptions.Left;
            EnableEffects(text);
            Assert.That(CountVisible(Render()), Is.Zero, "The initial left-aligned glyph must lie outside the clip.");
            text.alignment = TextAlignmentOptions.Center;
            Assert.That(CountVisible(Render()), Is.GreaterThan(100));
            foreach (SdfTextLayer layer in canvas.GetComponentsInChildren<SdfTextLayer>())
                Assert.That(layer.canvasRenderer.cull, Is.False, "Newly visible effects must uncull in the same rebuild.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AncestorMask_ClipsBothTextAndExpandedEffects(bool stencil)
        {
            var mask = (RectTransform)NewObject("Text Mask", typeof(RectTransform)).transform;
            mask.SetParent(canvas.transform, false);
            mask.sizeDelta = new Vector2(40, 80);
            if (stencil)
            {
                mask.gameObject.AddComponent<UnityEngine.UI.Image>();
                mask.gameObject.AddComponent<UnityEngine.UI.Mask>().showMaskGraphic = false;
            }
            else mask.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
            SdfText text = CreateText(mask, "AVAVA");
            EnableEffects(text);
            text.OutlineWidth = 1;
            text.ShadowEnabled = false;
            Color[] thin = Render();
            text.OutlineWidth = 8;
            Color[] pixels = Render();
            Assert.That(CountVisible(pixels), Is.GreaterThan(100));
            Assert.That(CountVisible(pixels), Is.GreaterThan(CountVisible(thin) + 10),
                "Changing effect style must also refresh a material derived for stencil masking.");
            for (int y = 0; y < Resolution; y++)
            for (int x = 0; x < Resolution; x++)
                if (Mathf.Abs(x + 0.5f - Resolution / 2f) > 22)
                    Assert.That(pixels[y * Resolution + x].a, Is.LessThan(0.02f),
                        "Effect leaked beyond the ancestor mask at " + x + "," + y);
        }

        [Test]
        public void CanvasGroupOnSource_FadesSiblingOutlineAndShadow()
        {
            SdfText text = CreateText(canvas.transform, "O");
            Color[] face = Render();
            EnableEffects(text);
            text.ShadowEnabled = false;
            Color[] opaque = Render();
            CanvasGroup group = text.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0.35f;
            Color[] faded = Render();
            int samples = 0;
            for (int i = 0; i < opaque.Length; i++)
            {
                if (face[i].a > 0.01f || opaque[i].a < 0.95f) continue;
                Assert.That(faded[i].a, Is.EqualTo(opaque[i].a * group.alpha).Within(0.045f),
                    "A CanvasGroup on the text itself must also fade its sibling effect.");
                samples++;
            }
            Assert.That(samples, Is.GreaterThan(50));
            group.alpha = 0;
            text.ShadowEnabled = true;
            Assert.That(CountVisible(Render()), Is.Zero);
        }

        [Test]
        public void NegativeShadowSpread_ContractsTheShadowSilhouette()
        {
            SdfText text = CreateText(canvas.transform, "O");
            Color[] face = Render();
            text.ShadowEnabled = true;
            text.ShadowColor = Color.black;
            text.ShadowOffset = new Vector2(20, -12);
            text.ShadowBlur = 0;
            text.ShadowSpread = 0;
            Color[] normal = Render();
            text.ShadowSpread = -3;
            Color[] contracted = Render();
            Assert.That(CountExteriorEffect(face, normal), Is.GreaterThan(100));
            Assert.That(CountExteriorEffect(face, contracted),
                Is.LessThan(CountExteriorEffect(face, normal) - 10),
                "Negative spread must shrink the shadow, not clamp to zero.");
        }

        [Test]
        public void Effects_PreserveSharedFontMaterialAndRenderImmediatelyBeforeSource()
        {
            string materialBefore = EditorJsonUtility.ToJson(font.material);
            SdfText text = CreateText(canvas.transform, "AVAVA");
            Vector2 preferred = text.GetPreferredValues();
            Vector2 rectangle = text.rectTransform.sizeDelta;
            EnableEffects(text);
            Render();
            Assert.That(text.GetPreferredValues(), Is.EqualTo(preferred));
            Assert.That(text.rectTransform.sizeDelta, Is.EqualTo(rectangle));
            SdfTextLayer[] layers = canvas.GetComponentsInChildren<SdfTextLayer>(true);
            Assert.That(layers.Length, Is.GreaterThan(0));
            foreach (SdfTextLayer layer in layers)
            {
                Assert.That(layer.Owner, Is.SameAs(text));
                Assert.That(layer.raycastTarget, Is.False);
                Transform root = layer.transform;
                while (root.parent != text.transform.parent && root.parent) root = root.parent;
                Assert.That(root.parent, Is.SameAs(text.transform.parent));
                Assert.That(root.GetSiblingIndex(), Is.EqualTo(text.transform.GetSiblingIndex() - 1));
            }
            text.OutlineWidth = 12;
            text.ShadowBlur = 8;
            text.RefreshEffects();
            Render();
            text.enabled = false;
            Assert.That(EditorJsonUtility.ToJson(font.material), Is.EqualTo(materialBefore),
                "Per-text styles must not modify the shared font material asset.");
        }

        private SdfText CreateText(Transform parent, string value)
        {
            var text = NewObject("SDF Text Test", typeof(RectTransform), typeof(SdfText)).GetComponent<SdfText>();
            text.transform.SetParent(parent, false);
            text.rectTransform.sizeDelta = new Vector2(240, 100);
            text.font = font;
            text.fontSize = 64;
            text.characterSpacing = -25;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.color = Color.white;
            text.text = value;
            text.OutlineEnabled = false;
            text.ShadowEnabled = false;
            return text;
        }

        private static void EnableEffects(SdfText text)
        {
            text.OutlineEnabled = true;
            text.OutlineWidth = 8;
            text.OutlineSoftness = 0;
            text.OutlineColor = new Color(0.05f, 0.02f, 0.02f, 1);
            text.ShadowEnabled = true;
            text.ShadowOffset = new Vector2(5, -5);
            text.ShadowBlur = 3;
            text.ShadowSpread = 2;
            text.ShadowColor = new Color(0.02f, 0.02f, 0.1f, 0.8f);
            text.RefreshEffects();
        }

        private TMP_FontAsset CreateFont(string characters, int samplingSize = 90, int padding = 20, int atlasSize = 512)
        {
            var source = AssetDatabase.LoadAssetAtPath<Font>(
                AssetDatabase.GUIDToAssetPath("e3265ab4bf004d28a9537516768c1c75"));
            Assert.That(source, Is.Not.Null);
            TMP_FontAsset result = TMP_FontAsset.CreateFontAsset(source, samplingSize, padding,
                GlyphRenderMode.SDFAA, atlasSize, atlasSize);
            generatedFonts.Add(result);
            result.name = "SDF Test Font " + characters;
            Assert.That(result.TryAddCharacters(characters), Is.True);
            result.atlasPopulationMode = AtlasPopulationMode.Static;
            return result;
        }

        private Color[] Render()
        {
            Canvas.ForceUpdateCanvases();
            if (GraphicsSettings.currentRenderPipeline == null) camera.Render();
            else
            {
                var request = new RenderPipeline.StandardRequest { destination = target };
                Assert.That(RenderPipeline.SupportsRenderRequest(camera, request), Is.True);
                RenderPipeline.SubmitRenderRequest(camera, request);
            }
            RenderTexture previous = RenderTexture.active;
            var readback = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false, true);
            try
            {
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, Resolution, Resolution), 0, 0);
                readback.Apply();
                foreach (SdfTextLayer layer in canvas.GetComponentsInChildren<SdfTextLayer>())
                {
                    Material material = layer.materialForRendering;
                    if (!material) continue;
                    Assert.That(material.shader.isSupported, Is.True);
                    foreach (var message in ShaderUtil.GetShaderMessages(material.shader))
                        Assert.That(message.severity.ToString(), Is.Not.EqualTo("Error"), message.message);
                }
                return readback.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(readback);
            }
        }

        private GameObject NewObject(string name, params Type[] components)
        {
            var result = new GameObject(name, components);
            SceneManager.MoveGameObjectToScene(result, previewScene);
            return result;
        }

        private static void AssertFaceUnchanged(Color[] face, Color[] effects)
        {
            int samples = 0;
            for (int i = 0; i < face.Length; i++)
            {
                int x = i % Resolution, y = i / Resolution;
                if (x == 0 || y == 0 || x == Resolution - 1 || y == Resolution - 1) continue;
                // Exclude the antialiased fringe: changing TMP padding can move its sampling fractionally.
                bool interior = true;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    Color pixel = face[i + dy * Resolution + dx];
                    interior &= pixel.a > 0.99f && pixel.r > 0.99f && pixel.g > 0.99f && pixel.b > 0.99f;
                }
                if (!interior) continue;
                Assert.That(effects[i].r, Is.GreaterThan(0.975f), "Outline or shadow covered a glyph face at pixel " + i);
                Assert.That(effects[i].g, Is.GreaterThan(0.975f), "Outline or shadow covered a glyph face at pixel " + i);
                Assert.That(effects[i].b, Is.GreaterThan(0.975f), "Outline or shadow covered a glyph face at pixel " + i);
                samples++;
            }
            Assert.That(samples, Is.GreaterThan(100), "The baseline must contain opaque white glyph interiors.");
        }

        private static int CountExteriorEffect(Color[] face, Color[] effects)
        {
            int count = 0;
            for (int i = 0; i < face.Length; i++)
                if (face[i].a < 0.01f && effects[i].a > 0.4f) count++;
            return count;
        }

        private static int CountVisible(Color[] pixels)
        {
            int count = 0;
            foreach (Color pixel in pixels) if (pixel.a > 0.02f) count++;
            return count;
        }

        private static void SaveCapture(string filename, Color[] pixels)
        {
            var texture = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false, true);
            try
            {
                texture.SetPixels(pixels);
                texture.Apply();
                string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Build/Validation"));
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, filename), texture.EncodeToPNG());
            }
            finally { Object.DestroyImmediate(texture); }
        }
    }
}
