using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Data;
using VRC.SDKBase;
using VRC.Udon;

[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class GoodFeatures : GoodBase
{
    public Toggle toggle;
    public Text legacyText;
    public TextMeshPro worldText;
    public UdonBehaviour other;
    public GoodFeatures peer;
    public Transform root;
    private int[][] jagged = new int[2][];
    private DataList names = new DataList();
    private float _volume = 1f;

    public float Volume
    {
        get => _volume;
        set { _volume = Mathf.Clamp01(value); UpdateLabel(); }
    }

    public override int Compute(int x) => base.Compute(x) * 2;
    protected override void Apply() { }

    private void Start()
    {
        Volume = 0.5f;
        int parsed;
        if (int.TryParse("42", out parsed)) Debug.Log(parsed);
        if (int.TryParse("7", out var p2)) Debug.Log(p2);
        string a = null;
        string b = a ?? "fallback";
        int sum = Sum(1, 2, 3);
        string kind = sum > 3 ? "big" : "small";
        switch (kind) { case "big": Debug.Log("B"); break; case "small": break; }
        foreach (char c in kind) { Debug.Log(c); }
        foreach (Transform child in root) { child.gameObject.SetActive(true); }
        jagged[0] = new int[] { 1, 2 };
        names.Add("x");
        Debug.Log(names.Count + " " + names[0].String);
        var self = GetComponent<GoodFeatures>();
        var rb = GetComponent<Rigidbody>();
        if (rb != null) rb.velocity = Vector3.zero;
        if (other != null) other.SendCustomEvent("Foo");
        if (peer != null) peer.SendCustomEvent(nameof(_PublicNoArg));
        SendCustomEventDelayedFrames(nameof(_PublicNoArg), 1);
        InteractionText = "押す";
        DisableInteractive = false;
        int clamped = 150.Clamp100();
        legacyText.text = GoodHelpers.Percent(Volume) + $" {clamped:D3} {default(int)}";
        worldText.text = string.Format("{0}/{1}", 1, 2);
        toggle.SetIsOnWithoutNotify(true);
        Color col = Color.Lerp(Color.red, Color.white, 0.5f);
        Debug.Log(col + " " + Time.time + " " + nameof(Volume));
        var pl = VRCPlayerApi.GetPlayerById(1);
        if (Utilities.IsValid(pl) && pl.isMaster) Debug.Log(pl.GetPosition());
        long id = GetUdonTypeID<GoodFeatures>();
        Debug.Log(id);
        var go = Instantiate(gameObject);
        Destroy(go);
        float[] arr = new float[3];
        System.Array.Copy(arr, new float[3], 3);
        Debug.Log(arr.Length);
        const int localConst = 5;
        Debug.Log(localConst << 1);
    }

    public void _PublicNoArg() { }
    private int Sum(params int[] values) { int s = 0; for (int i = 0; i < values.Length; i++) s += values[i]; return s; }
    private void UpdateLabel(string prefix = "Vol") { if (legacyText != null) legacyText.text = prefix + _volume; }

    [RecursiveMethod]
    private int Fact(int n) => n <= 1 ? 1 : n * Fact(n - 1);
}
