// Tests for the Unity-independent part of Avatar Checker (Editor/Core). Run by tools/verify_checker.py.
using System;
using System.Collections.Generic;
using Naoking.AvatarChecker;

public static class CoreTests
{
    static int failures;

    static void Eq<T>(T want, T got, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(want, got)) { failures++; Console.WriteLine("FAIL: " + what + ": want " + want + ", got " + got); }
    }

    public static int Main()
    {
        // boundaries are inclusive (value <= limit)
        Eq(Rank.Excellent, PerformanceRank.Rate(Platform.PC, PerformanceRank.Triangles, 32000), "pc tris 32000");
        Eq(Rank.Good, PerformanceRank.Rate(Platform.PC, PerformanceRank.Triangles, 32001), "pc tris 32001");
        Eq(Rank.Good, PerformanceRank.Rate(Platform.PC, PerformanceRank.Triangles, 70000), "pc tris 70000");
        Eq(Rank.VeryPoor, PerformanceRank.Rate(Platform.PC, PerformanceRank.Triangles, 70001), "pc tris 70001");
        Eq(Rank.Poor, PerformanceRank.Rate(Platform.PC, PerformanceRank.Lights, 1), "pc lights 1");
        Eq(Rank.Medium, PerformanceRank.Rate(Platform.Mobile, PerformanceRank.Triangles, 15000), "mobile tris 15000");
        Eq(Rank.VeryPoor, PerformanceRank.Rate(Platform.Mobile, PerformanceRank.PhysBoneComponents, 9), "mobile pb 9");
        Eq(false, PerformanceRank.IsRanked(Platform.Mobile, PerformanceRank.Lights), "mobile lights unranked");
        Eq(Rank.Excellent, PerformanceRank.Rate(Platform.Mobile, PerformanceRank.Lights, 5), "unranked metric is Excellent");
        Eq((double?)75, PerformanceRank.Limit(Platform.PC, PerformanceRank.TextureMemoryMB, Rank.Good), "pc tex good limit");
        Eq((double?)null, PerformanceRank.Limit(Platform.PC, PerformanceRank.Bones, Rank.VeryPoor), "no very poor limit");

        // every PC table has 4 non-decreasing limits; mobile metrics are a subset of PC metrics
        foreach (var p in new[] { Platform.PC, Platform.Mobile })
            foreach (var m in PerformanceRank.Metrics(p))
            {
                double prev = double.MinValue;
                for (var r = Rank.Excellent; r <= Rank.Poor; r++)
                {
                    var l = PerformanceRank.Limit(p, m, r).Value;
                    if (l < prev) { failures++; Console.WriteLine("FAIL: limits decrease for " + p + " " + m); }
                    prev = l;
                }
                if (!PerformanceRank.IsRanked(Platform.PC, m)) { failures++; Console.WriteLine("FAIL: " + m + " ranked on mobile only"); }
            }

        // overall rank is the worst metric
        var stats = new Dictionary<string, double> { { PerformanceRank.Triangles, 20000 }, { PerformanceRank.MaterialSlots, 10 } };
        Eq(Rank.Medium, PerformanceRank.Overall(Platform.PC, stats), "overall = worst");

        // JSON: escaping, numbers, null cost, invariant culture
        System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        var rep = new CheckReport { AvatarName = "霧島 \"改\"\\\n" };
        rep.Stats[PerformanceRank.TextureMemoryMB] = 12.3456;
        rep.Stats[PerformanceRank.Lights] = 1;
        rep.Add(Severity.Error, "missing-script", "Missing\tスクリプト", "Armature/Hips");
        var json = rep.ToJson();
        Console.WriteLine(json);
        Expect(json, "\"avatar\": \"霧島 \\\"改\\\"\\\\\\n\"");
        Expect(json, "\"textureMemoryMB\": { \"value\": 12.35, \"pc\": \"Excellent\", \"mobile\": \"Good\" }");
        Expect(json, "\"lights\": { \"value\": 1, \"pc\": \"Poor\", \"mobile\": null }");
        Expect(json, "\"cost\": null, \"budget\": 256");
        Expect(json, "\"rank\": { \"pc\": \"Poor\", \"mobile\": \"Good\" }");
        Expect(json, "{ \"severity\": \"error\", \"code\": \"missing-script\", \"path\": \"Armature/Hips\", \"message\": \"Missing\\tスクリプト\" }");
        Eq(1, rep.Count(Severity.Error), "error count");

        var empty = new CheckReport().ToJson();
        Expect(empty, "\"stats\": {},");
        Expect(empty, "\"issues\": []");

        Console.WriteLine(failures == 0 ? "CORE OK" : failures + " core failure(s)");
        return failures == 0 ? 0 : 1;
    }

    static void Expect(string json, string part)
    {
        if (!json.Contains(part)) { failures++; Console.WriteLine("FAIL: JSON lacks " + part); }
    }
}
