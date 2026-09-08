using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UI;
using UnityEngine;
using Image = UnityEngine.UI.Image;

namespace SDFUI.Editor
{
    [CustomEditor(typeof(SdfImage)), CanEditMultipleObjects]
    public sealed class SdfImageEditor : ImageEditor
    {
        private static readonly int[] Sizes = { 64, 128, 256, 512, 1024 };
        private static readonly string[] SizeLabels = { "64", "128", "256", "512", "1024" };
        private SerializedProperty outlineEnabled, shadowEnabled;
        private bool showBakeSettings;

        protected override void OnEnable()
        {
            base.OnEnable();
            outlineEnabled = serializedObject.FindProperty("outlineEnabled");
            shadowEnabled = serializedObject.FindProperty("shadowEnabled");
        }

        public override void OnInspectorGUI()
        {
            // Keep all standard Image controls and their native layout/behavior.
            base.OnInspectorGUI();
            serializedObject.Update();
            bool ready = true, hasSource = false, busy = false;
            string status = string.Empty;
            foreach (SdfImage image in targets)
            {
                hasSource |= image.SourceSprite;
                ready &= image.SdfData && image.SdfData.IsValid;
                string current = SdfBakeQueue.GetStatus(AssetDatabase.GetAssetPath(image.SourceSprite));
                if (IsBusy(current)) { busy = true; status = current; }
            }

            if (hasSource)
            {
                EditorGUILayout.Space(4);
                if (busy)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(status + "…", EditorStyles.miniLabel);
                        if (GUILayout.Button("Cancel", EditorStyles.miniButton, GUILayout.Width(64)))
                            ChangeGeneration(false);
                    }
                }
                else if (!ready)
                {
                    EditorGUILayout.LabelField("Generate once to unlock outline and shadow.", EditorStyles.miniLabel);
                    using (new EditorGUI.DisabledScope(!CanGenerate()))
                        if (GUILayout.Button("Generate SDF", GUILayout.Height(28))) ChangeGeneration(true);
                    if (!CanGenerate())
                        EditorGUILayout.HelpBox("Generation requires an imported Sprite inside Assets.", MessageType.Info);
                }
                else
                    EditorGUILayout.LabelField("✓  SDF ready", EditorStyles.miniLabel);

                foreach (SdfImage image in targets)
                {
                    string error = SdfBakeQueue.GetStatus(AssetDatabase.GetAssetPath(image.SourceSprite));
                    if (error.StartsWith("Error:", System.StringComparison.Ordinal))
                    { EditorGUILayout.HelpBox(error, MessageType.Error); break; }
                }
            }

            if (ready)
            {
                EditorGUILayout.Space(4);
                bool supported = true;
                foreach (SdfImage image in targets)
                    supported &= image.type == Image.Type.Simple || image.type == Image.Type.Sliced && image.fillCenter;
                if (!supported)
                    EditorGUILayout.HelpBox("SDF effects use Simple or Sliced with Fill Center enabled. " +
                        "Other modes render as a standard Unity Image.", MessageType.Info);
                using (new EditorGUI.DisabledScope(!supported))
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        if (ToggleSection(outlineEnabled, "Outline"))
                        {
                            DrawOutlineColor();
                            Field("outlineWidth", "Width");
                            Field("outlinePosition", "Position");
                            Field("outlineSoftness", "Softness");
                        }
                    }
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        if (ToggleSection(shadowEnabled, "Shadow"))
                        {
                            Field("shadowColor", "Color");
                            Field("shadowOffset", "Offset");
                            Field("shadowBlur", "Blur");
                            Field("shadowSpread", "Spread");
                        }
                    }
                }
            }

            serializedObject.ApplyModifiedProperties();
            if (hasSource) DrawBakeSettings(ready);
            DrawLegacyBindingCleanup();
        }

        private void Field(string name, string label) =>
            EditorGUILayout.PropertyField(serializedObject.FindProperty(name), new GUIContent(label));

        private void DrawOutlineColor()
        {
            var useTextureColor = serializedObject.FindProperty("outlineUseTextureColor");
            EditorGUILayout.PropertyField(useTextureColor, new GUIContent("Use Texture Color"));
            if (!useTextureColor.boolValue || useTextureColor.hasMultipleDifferentValues)
                Field("outlineColor", "Color");
            if (useTextureColor.boolValue || useTextureColor.hasMultipleDifferentValues)
            {
                var intensity = serializedObject.FindProperty("outlineTextureColorIntensity");
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(intensity, new GUIContent("Intensity"));
                if (EditorGUI.EndChangeCheck()) intensity.floatValue = Mathf.Max(0, intensity.floatValue);
                var opacity = serializedObject.FindProperty("outlineColor").FindPropertyRelative("a");
                EditorGUILayout.Slider(opacity, 0, 1, new GUIContent("Opacity"));
            }
        }

        private static bool ToggleSection(SerializedProperty toggle, string title)
        {
            var rect = EditorGUILayout.GetControlRect();
            EditorGUI.BeginProperty(rect, new GUIContent(title), toggle);
            EditorGUI.showMixedValue = toggle.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            bool value = EditorGUI.ToggleLeft(rect, title, toggle.boolValue, EditorStyles.boldLabel);
            if (EditorGUI.EndChangeCheck()) toggle.boolValue = value;
            EditorGUI.showMixedValue = false;
            EditorGUI.EndProperty();
            return toggle.boolValue || toggle.hasMultipleDifferentValues;
        }

        private bool CanGenerate()
        {
            foreach (SdfImage image in targets)
            {
                string path = AssetDatabase.GetAssetPath(image.SourceSprite);
                if (!image.SourceSprite || !path.StartsWith("Assets/", System.StringComparison.Ordinal) ||
                    !(AssetImporter.GetAtPath(path) is TextureImporter)) return false;
            }
            return true;
        }

        private void ChangeGeneration(bool enabled)
        {
            serializedObject.ApplyModifiedProperties();
            var paths = new HashSet<string>();
            foreach (SdfImage image in targets)
            {
                string path = AssetDatabase.GetAssetPath(image.SourceSprite);
                if (!path.StartsWith("Assets/", System.StringComparison.Ordinal) ||
                    !(AssetImporter.GetAtPath(path) is TextureImporter)) continue;
                if (paths.Add(path))
                {
                    var settings = SdfTextureSettings.Get(path);
                    settings.enabled = enabled;
                    // Explicit Generate also retries a previously failed/cancelled generation.
                    SdfBakeQueue.Cancel(path);
                    SdfTextureSettings.Set(path, settings);
                }
                SdfSourceImporter.RefreshTarget(image);
            }
        }

        private void DrawBakeSettings(bool ready)
        {
            showBakeSettings = EditorGUILayout.Foldout(showBakeSettings, "SDF Settings", true);
            if (!showBakeSettings) return;
            if (targets.Length != 1 || !CanGenerate())
            {
                EditorGUILayout.HelpBox("Select one image with a sprite inside Assets to edit its SDF settings.", MessageType.Info);
                return;
            }
            var image = (SdfImage)target;
            string path = AssetDatabase.GetAssetPath(image.SourceSprite);
            var settings = SdfTextureSettings.Get(path);
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUI.BeginChangeCheck();
                settings.enabled = EditorGUILayout.Toggle("Auto Update", settings.enabled);
                settings.maxSize = EditorGUILayout.IntPopup("Maximum Size", settings.maxSize, SizeLabels, Sizes);
                settings.padding = EditorGUILayout.IntSlider("Padding", settings.padding, 4, 128);
                settings.range = EditorGUILayout.Slider("Distance Range", settings.range, 4, settings.padding);
                settings.alphaThreshold = EditorGUILayout.Slider("Alpha Threshold", settings.alphaThreshold, 0.01f, 0.99f);
                if (EditorGUI.EndChangeCheck())
                {
                    SdfTextureSettings.Set(path, settings);
                    SdfSourceImporter.RefreshTarget(image);
                }
                EditorGUILayout.HelpBox("Settings are shared by sprites in this source texture. " +
                    "Auto Update refreshes the SDF when the source changes.", MessageType.None);
                if (ready && GUILayout.Button("Refresh SDF")) ChangeGeneration(true);
            }
        }

        private void DrawLegacyBindingCleanup()
        {
            if (targets.Length != 1) return;
            var image = (SdfImage)target;
            var binding = image.GetComponent<SdfAutoBake>();
            if (!binding) return;
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox("Sprite and generation now live in this component. The old Auto Bake component can be removed.", MessageType.None);
            if (!GUILayout.Button("Remove Legacy Auto Bake")) return;
            Undo.RecordObject(image, "Move SDF Source Into Image");
            binding.Resolve();
            image.RefreshSdf();
            EditorUtility.SetDirty(image);
            PrefabUtility.RecordPrefabInstancePropertyModifications(image);
            Undo.DestroyObjectImmediate(binding);
        }

        private static bool IsBusy(string status) => status == "Queued" || status.StartsWith("Baking ", System.StringComparison.Ordinal);

        public override bool RequiresConstantRepaint()
        {
            foreach (SdfImage image in targets)
                if (IsBusy(SdfBakeQueue.GetStatus(AssetDatabase.GetAssetPath(image.SourceSprite)))) return true;
            return false;
        }

        [MenuItem("GameObject/UI/SDF Image", false, 2030)]
        private static void CreateImage(MenuCommand command)
        {
            var selectedSprite = Selection.activeObject as Sprite;
            var parent = command.context as GameObject;
            if (!parent) parent = Selection.activeGameObject;
            Canvas canvas = parent ? parent.GetComponentInParent<Canvas>() : null;
            if (!canvas)
            {
                var canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas),
                    typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
                Undo.RegisterCreatedObjectUndo(canvasObject, "Create SDF Canvas");
                canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = canvasObject.GetComponent<UnityEngine.UI.CanvasScaler>();
                scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1080, 1920);
                scaler.matchWidthOrHeight = 0.5f;
                parent = canvasObject;
            }
            var imageObject = new GameObject("SDF Image", typeof(RectTransform), typeof(SdfImage));
            Undo.RegisterCreatedObjectUndo(imageObject, "Create SDF Image");
            GameObjectUtility.SetParentAndAlign(imageObject, parent ? parent : canvas.gameObject);
            var image = imageObject.GetComponent<SdfImage>();
            image.rectTransform.sizeDelta = new Vector2(160, 160);
            image.raycastTarget = false;
            image.ShadowEnabled = false;
            if (selectedSprite) image.sprite = selectedSprite;
            Selection.activeGameObject = imageObject;
        }
    }
}
