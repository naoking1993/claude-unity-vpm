using UdonSharp;
using UnityEngine;

// Expected: USL125 for System.IO.FileMode (an extern enum no exposed Udon API uses). ForceMode is fine.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class BadExternEnum : UdonSharpBehaviour
{
    public System.IO.FileMode mode;
    public ForceMode okMode = ForceMode.Impulse;
}
