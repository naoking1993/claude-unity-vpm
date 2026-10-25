using UdonSharp;
using VRC.SDK3.UdonNetworkCalling;
using VRC.Udon.Common.Interfaces;

// Expected: USL163 (network event received by a BehaviourSyncMode.None behaviour is never delivered).
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class BadNetworkNone : UdonSharpBehaviour
{
    public override void Interact()
    {
        SendCustomNetworkEvent(NetworkEventTarget.All, nameof(Ping));
    }

    [NetworkCallable]
    public void Ping() { }
}
