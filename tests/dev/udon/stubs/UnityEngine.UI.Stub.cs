// ============================================================================================
// COMPILE-CHECK STUB ONLY - NOT THE REAL com.unity.ugui PACKAGE. Never ship or copy into a project.
// The UGUI package (UnityEngine.UI.dll) is not available offline here, so this file declares the
// tiny subset of its public API that our U# samples touch, with the same namespaces, type names,
// inheritance chain and member signatures as com.unity.ugui 1.0.0 (Unity 2022.3), so that symbol
// resolution and Udon extern names ("UnityEngineUISlider.__get_value__SystemSingle") match.
// Bodies are dummies. Members not listed here simply do not exist for the harness (=> CS1061).
// ============================================================================================
using UnityEngine;
using UnityEngine.Events;

namespace UnityEngine.EventSystems
{
    public abstract class UIBehaviour : MonoBehaviour
    {
        public virtual bool IsActive() => isActiveAndEnabled;
    }
}

namespace UnityEngine.UI
{
    public abstract class Graphic : UnityEngine.EventSystems.UIBehaviour
    {
        public virtual Color color { get; set; }
        public bool raycastTarget { get; set; }
    }

    public abstract class MaskableGraphic : Graphic
    {
        public bool maskable { get; set; }
    }

    public class Text : MaskableGraphic
    {
        public virtual string text { get; set; }
        public int fontSize { get; set; }
    }

    public class Image : MaskableGraphic
    {
        public float fillAmount { get; set; }
    }

    public class Selectable : UnityEngine.EventSystems.UIBehaviour
    {
        public bool interactable { get; set; }
        public virtual bool IsInteractable() => interactable;
    }

    public class Button : Selectable
    {
        public class ButtonClickedEvent : UnityEvent { }
        public ButtonClickedEvent onClick { get; set; }
    }

    public class Toggle : Selectable
    {
        public class ToggleEvent : UnityEvent<bool> { }
        public ToggleEvent onValueChanged;
        public bool isOn { get; set; }
        public void SetIsOnWithoutNotify(bool value) { }
    }

    public class Slider : Selectable
    {
        public class SliderEvent : UnityEvent<float> { }
        public float minValue { get; set; }
        public float maxValue { get; set; }
        public bool wholeNumbers { get; set; }
        public virtual float value { get; set; }
        public float normalizedValue { get; set; }
        public SliderEvent onValueChanged { get; set; }
        public virtual void SetValueWithoutNotify(float input) { }
    }
}
