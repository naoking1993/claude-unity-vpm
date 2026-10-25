using UnityEditor;
using UnityEngine;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// Full type names of VRChat SDK components read via <see cref="CctSerialized"/> (no compile-time SDK
    /// dependency). Keep every name here so a future SDK rename is a one-line fix.
    /// </summary>
    public static class CctVrcTypes
    {
        // Avatars SDK
        public const string AvatarDescriptor = "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor";
        public const string ExpressionParameters = "VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters";
        public const string ExpressionsMenu = "VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu";

        // Shared (avatar + world) dynamics
        public const string PhysBone = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone";
        public const string PhysBoneCollider = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider";
        public const string ContactSender = "VRC.SDK3.Dynamics.Contact.Components.VRCContactSender";
        public const string ContactReceiver = "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver";

        // Worlds SDK
        public const string SceneDescriptor = "VRC.SDK3.Components.VRCSceneDescriptor";
        public const string UdonBehaviour = "VRC.Udon.UdonBehaviour";

        // Both
        public const string PipelineManager = "VRC.Core.PipelineManager";

        /// <summary>Blueprint ID from the PipelineManager on the same GameObject ("" when not uploaded yet, null when absent).</summary>
        public static string ReadBlueprintId(GameObject go)
        {
            if (go == null) return null;
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null || !CctSerialized.IsType(c, PipelineManager)) continue;
                using (var so = new SerializedObject(c))
                    return CctSerialized.GetString(so.FindProperty("blueprintId"), "");
            }
            return null;
        }
    }
}
