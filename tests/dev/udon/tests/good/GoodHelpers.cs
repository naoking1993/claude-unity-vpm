using UnityEngine;

public static class GoodHelpers
{
    public const float Epsilon = 0.001f;
    public static int Clamp100(this int v) => Mathf.Clamp(v, 0, 100);
    public static string Percent(float v) => Mathf.RoundToInt(v * 100f) + "%";
}
