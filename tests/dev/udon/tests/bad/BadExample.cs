using System.Collections.Generic;
using System.Linq;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

public interface IFoo { void Bar(); }
public struct MyStruct { public int x; }
public class PlainClass { public int y; }

public class BadExample : UdonSharpBehaviour
{
    public static int counter;
    private List<int> list;
    private int[,] grid;
    [UdonSynced] private GameObject syncedRef;
    [UdonSynced(UdonSyncMode.Linear)] private float lerped;
    [FieldChangeCallback("Missing")] private int watched;
    private Vector3 initPos = Networking.LocalPlayer.GetPosition();

    public BadExample() { }

    class Nested { }

    private void Awake() { }

    public T Generic<T>(T v) => v;

    public void OnPlayerJoined() { }
    public void OnPlayerLeft(int id) { }

    void Start()
    {
        try { Debug.Log(1); } catch { }
        System.Func<int, int> f = x => x + 1;
        int Local() => 3;
        var q = from s in new int[3] select s;
        var arr = new int[] { 1, 2 };
        var first = arr.FirstOrDefault();
        Transform t = null;
        var p = t?.position;
        object o = gameObject;
        if (o is GameObject) { }
        var go = o as GameObject;
        var v = new Vector3 { x = 1 };
        Vector3 w = new();
        int.TryParse("1", out _);
        switch (o) { case GameObject g2: break; }
        var tup = (1, 2);
        string s2 = null; s2 ??= "x";
        foreach (var i in list) { }
        var pc = new PlainClass();
        var ty = typeof(BadExample);
        gameObject.SendMessage("Foo");
        SendCustomEvent("NotThere");
        SendCustomEvent(nameof(PrivateThing));
        SendCustomNetworkEvent(NetworkEventTarget.All, nameof(_Hidden));
        Recurse(3);
        var lp = Networking.LocalPlayer.displayName;
        StartCoroutine(null);
        lock (this) { }
        throw new System.Exception();
    }

    private void PrivateThing() { }
    public void _Hidden() { }
    private int Recurse(int n) => n <= 0 ? 0 : Recurse(n - 1);
    System.Collections.IEnumerator Co() { yield return null; }
}

public class SecondBehaviour : UdonSharpBehaviour { }
