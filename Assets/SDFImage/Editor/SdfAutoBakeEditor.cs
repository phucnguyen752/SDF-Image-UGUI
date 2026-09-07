using UnityEditor;

namespace SDFUI.Editor
{
    [CustomEditor(typeof(SdfAutoBake))]
    public sealed class SdfAutoBakeEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("Source Image and Generate SDF are now built into SDF Image. " +
                "Use Remove Legacy Auto Bake on that component to remove this old helper.", MessageType.Info);
        }
    }
}
