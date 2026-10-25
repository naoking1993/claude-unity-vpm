using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;
using VRC.SDK3.UdonNetworkCalling;

public enum GoodMode { A, B, C }

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class GoodExample : UdonSharpBehaviour
{
    public GameObject toggleObject;
    public TextMeshProUGUI label;
    public Slider slider;
    public VRCPickup pickup;
    public VRCObjectSync objectSync;
    public AudioSource[] sources;
    public Transform target;
    public GoodMode mode = GoodMode.B;

    [UdonSynced, FieldChangeCallback(nameof(SyncedToggle))]
    private bool _syncedToggle;
    [UdonSynced] private int[] _scores = new int[4];
    private VRCPlayerApi[] _players = new VRCPlayerApi[16];
    private const int MaxLog = 5;

    public bool SyncedToggle
    {
        set { _syncedToggle = value; if (toggleObject != null) toggleObject.SetActive(value); }
        get => _syncedToggle;
    }

    private void Start()
    {
        var lp = Networking.LocalPlayer;
        if (Utilities.IsValid(lp)) Debug.Log($"hello {lp.displayName} {lp.playerId}");
        SendCustomEventDelayedSeconds(nameof(_Tick), 1f);
        float v = slider != null ? slider.value : 1f;
        foreach (var s in sources) { if (s != null) s.volume = v * 0.5f; }
        Vector3 p = transform.position + Vector3.up * 2f;
        target.SetPositionAndRotation(p, Quaternion.identity);
        int n = VRCPlayerApi.GetPlayerCount();
        VRCPlayerApi.GetPlayers(_players);
        string s2 = n.ToString() + "人";
        label.text = s2;
        switch (mode) { case GoodMode.A: break; case GoodMode.B: Debug.Log("b"); break; default: break; }
        if (pickup != null && pickup.IsHeld) pickup.Drop();
        if (objectSync != null) objectSync.Respawn();
        int idx = System.Array.IndexOf(sources, null);
        Debug.Log(Mathf.RoundToInt(v * 100f) + "% " + idx);
        string name = mode.ToString();
        if (name == null) return;
    }

    public void _Tick() { }

    public override void Interact()
    {
        var lp = Networking.LocalPlayer;
        if (!Utilities.IsValid(lp)) return;
        if (!Networking.IsOwner(gameObject)) Networking.SetOwner(lp, gameObject);
        SyncedToggle = !SyncedToggle;
        RequestSerialization();
        lp.TeleportTo(target.position, target.rotation, VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint, false);
        SendCustomNetworkEvent(NetworkEventTarget.All, nameof(Ping), 3);
    }

    [NetworkCallable]
    public void Ping(int value) { Debug.Log(value); }

    public override void OnPlayerJoined(VRCPlayerApi player) { }
    public override void OnPlayerTriggerEnter(VRCPlayerApi player) { if (player.isLocal) Debug.Log("in"); }
    public override void OnDeserialization() { }
    private int Add(int a, int b) => a + b;
    private int Add(int a, int b, int c) => a + b + c;
    private static int Twice(int a) => a * 2;
}
