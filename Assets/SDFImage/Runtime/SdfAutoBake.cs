using UnityEngine;

namespace SDFUI
{
    /// <summary>Resolves a source Sprite's attached SDF data. Editor baking uses the source texture's settings.</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SdfImage))]
    [AddComponentMenu("")]
    public sealed class SdfAutoBake : MonoBehaviour
    {
        [SerializeField] private Sprite source;
        [SerializeField, HideInInspector] private bool sourceSeeded;
#if UNITY_EDITOR
        public static event System.Action<SdfAutoBake> Changed;
#endif

        public SdfImage Target => GetComponent<SdfImage>();

        /// <summary>Source Sprite whose attached baked data is used in both the Editor and player.</summary>
        public Sprite Source
        {
            get => source;
            set
            {
                if (source == value) return;
                source = value;
                Resolve();
#if UNITY_EDITOR
                Changed?.Invoke(this);
#endif
            }
        }

        /// <summary>Refreshes the target after a source change or completed import; no work runs per frame.</summary>
        public void Resolve()
        {
            var target = Target;
            if (target)
            {
                // Compatibility only: the Image's standard source becomes authoritative once populated.
                if (!sourceSeeded && source)
                {
                    if (!target.SourceSprite)
                        target.sprite = source;
                    sourceSeeded = true;
                }
                target.RefreshSdf();
            }
        }

        private void OnEnable()
        {
            Resolve();
#if UNITY_EDITOR
            Changed?.Invoke(this);
#endif
        }

#if UNITY_EDITOR
        private void OnValidate() => Changed?.Invoke(this);
#endif
    }
}
