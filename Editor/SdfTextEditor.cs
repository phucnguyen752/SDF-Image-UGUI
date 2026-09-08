using TMPro;
using TMPro.EditorUtilities;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace SDFUI.Editor
{
    [CustomEditor(typeof(SdfText)), CanEditMultipleObjects]
    public sealed class SdfTextEditor : TMP_EditorPanelUI
    {
        private SerializedProperty outlineEnabled, outlineWidth, outlineSoftness, outlineColor;
        private SerializedProperty shadowEnabled, shadowOffset, shadowBlur, shadowSpread, shadowColor;

        protected override void OnEnable()
        {
            base.OnEnable();
            outlineEnabled = serializedObject.FindProperty("sdfOutlineEnabled");
            outlineWidth = serializedObject.FindProperty("sdfOutlineWidth");
            outlineSoftness = serializedObject.FindProperty("sdfOutlineSoftness");
            outlineColor = serializedObject.FindProperty("sdfOutlineColor");
            shadowEnabled = serializedObject.FindProperty("sdfShadowEnabled");
            shadowOffset = serializedObject.FindProperty("sdfShadowOffset");
            shadowBlur = serializedObject.FindProperty("sdfShadowBlur");
            shadowSpread = serializedObject.FindProperty("sdfShadowSpread");
            shadowColor = serializedObject.FindProperty("sdfShadowColor");
        }

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            serializedObject.Update();
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("SDF Effects", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Draws every glyph's shadow and outline behind the whole label. " +
                "Sizes use local Canvas units; the font atlas padding limits width and blur.", MessageType.None);

            bool supported = true;
            foreach (SdfText text in targets)
                supported &= text.EffectsSupported;
            if (!supported)
                EditorGUILayout.HelpBox("Place Canvas, Mask and RectMask2D components on a parent object. " +
                    "SDF Text effects do not support these components on the text object itself.", MessageType.Warning);

            using (new EditorGUI.DisabledScope(!supported))
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (ToggleSection(outlineEnabled, "Outline"))
                    {
                        Field(outlineColor, "Color");
                        Field(outlineWidth, "Width");
                        Field(outlineSoftness, "Softness");
                    }
                }
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (ToggleSection(shadowEnabled, "Shadow"))
                    {
                        Field(shadowColor, "Color");
                        Field(shadowOffset, "Offset");
                        Field(shadowBlur, "Blur");
                        Field(shadowSpread, "Spread");
                    }
                }
            }

            if (serializedObject.ApplyModifiedProperties())
                foreach (SdfText text in targets) text.RefreshEffects();
        }

        private static void Field(SerializedProperty property, string label) =>
            EditorGUILayout.PropertyField(property, new GUIContent(label));

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

        [MenuItem("GameObject/UI/SDF Text", false, 2031)]
        private static void CreateText(MenuCommand command)
        {
            var parent = command.context as GameObject;
            if (!parent) parent = Selection.activeGameObject;
            Canvas canvas = parent ? parent.GetComponentInParent<Canvas>() : null;
            if (!canvas)
            {
                var canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas),
                    typeof(CanvasScaler), typeof(GraphicRaycaster));
                StageUtility.PlaceGameObjectInCurrentStage(canvasObject);
                Undo.RegisterCreatedObjectUndo(canvasObject, "Create SDF Canvas");
                canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = canvasObject.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1080, 1920);
                scaler.matchWidthOrHeight = 0.5f;
                parent = canvasObject;
            }

            var textObject = new GameObject("SDF Text", typeof(RectTransform), typeof(SdfText));
            StageUtility.PlaceGameObjectInCurrentStage(textObject);
            Undo.RegisterCreatedObjectUndo(textObject, "Create SDF Text");
            GameObjectUtility.SetParentAndAlign(textObject, parent ? parent : canvas.gameObject);
            var text = textObject.GetComponent<SdfText>();
            text.rectTransform.sizeDelta = new Vector2(300, 100);
            text.text = "SDF Text";
            text.fontSize = TMP_Settings.defaultFontSize;
            text.alignment = TextAlignmentOptions.Center;
            text.raycastTarget = false;
            text.OutlineEnabled = true;
            text.ShadowEnabled = false;
            Selection.activeGameObject = textObject;
        }
    }
}
