// ============================================================================================
// COMPILE-CHECK STUB ONLY - NOT THE REAL TextMeshPro PACKAGE. Never ship or copy into a project.
// TextMeshPro (Unity.TextMeshPro.dll, com.unity.textmeshpro 3.0.x / bundled in ugui 2.0) is not
// available offline here. This declares the minimal public API our U# samples use with the real
// namespace, names and inheritance (TMP_Text : MaskableGraphic; TextMeshProUGUI/TextMeshPro : TMP_Text)
// so Udon extern names such as "TMProTMP_Text.__set_text__SystemString__SystemVoid" resolve correctly.
// ============================================================================================
using UnityEngine;

namespace TMPro
{
    public abstract class TMP_Text : UnityEngine.UI.MaskableGraphic
    {
        public virtual string text { get; set; }
        public float fontSize { get; set; }
    }

    /// <summary>TextMeshPro - Text (UI) (Canvas 上の UI テキスト)</summary>
    public class TextMeshProUGUI : TMP_Text { }

    /// <summary>TextMeshPro - Text (3D ワールド空間テキスト, MeshRenderer)</summary>
    public class TextMeshPro : TMP_Text { }
}
