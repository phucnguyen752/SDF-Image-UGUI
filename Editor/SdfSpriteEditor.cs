using UnityEditor;
using UnityEngine;

namespace SDFUI.Editor
{
    [CustomEditor(typeof(SdfSprite))]
    public sealed class SdfSpriteEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            using (new EditorGUI.DisabledScope(true)) DrawDefaultInspector();
            var sprite = (SdfSprite)target;
            EditorGUILayout.HelpBox("Generated data belongs to the original source sprite. " +
                "Use Generate SDF on its source or the SDF Image component to update it automatically.", MessageType.Info);
            if (sprite.SourceSprite && GUILayout.Button("Select Source Sprite"))
                Selection.activeObject = sprite.SourceSprite;
        }

        public override bool HasPreviewGUI() => ((SdfSprite)target).ColorTexture;
        public override void OnPreviewGUI(Rect rectangle, GUIStyle background)
        {
            var texture = ((SdfSprite)target).ColorTexture;
            if (texture) EditorGUI.DrawPreviewTexture(rectangle, texture, null, ScaleMode.ScaleToFit);
        }
    }
}
