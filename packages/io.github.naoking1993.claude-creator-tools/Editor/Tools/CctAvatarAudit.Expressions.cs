using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    // data.expressions: Expression Parameters (sync cost, list), Expressions Menu tree (menus, controls, depth) and the menu /
    // parameter checks of the SDK 3.10.5 builder (VRCSdkControlPanelAvatarBuilder.ValidateFeatures) plus documented limits the
    // builder does not enforce (8 controls per menu, 8192 parameters). Read through SerializedObject only
    // (VRCExpressionParameters / VRCExpressionsMenu field names verified with FieldCheck).
    public static partial class CctAvatarAudit
    {
        // VRCExpressionParameters+ValueType (FieldCheck): Int=0, Float=1, Bool=2.
        static readonly string[] ParamTypeNames = { "Int", "Float", "Bool" };
        const int ParamTypeInt = 0, ParamTypeFloat = 1, ParamTypeBool = 2;

        // VRCExpressionsMenu+Control+ControlType (FieldCheck): Button=101, Toggle=102, SubMenu=103, TwoAxisPuppet=201,
        // FourAxisPuppet=202, RadialPuppet=203.
        const int ControlSubMenu = 103, ControlTwoAxis = 201, ControlFourAxis = 202, ControlRadial = 203;

        /// <summary>VRCExpressionParameters.MAX_PARAMETER_COUNT (SDK 3.10.5; docs avatars/animator-parameters "up to 8192").</summary>
        const int MaxParameterCount = 8192;
        /// <summary>VRCExpressionsMenu.MAX_CONTROLS (SDK 3.10.5; docs avatars/expression-menu-and-controls "Up to 8 controls").</summary>
        const int MaxMenuControls = 8;
        /// <summary>VRCSdkControlPanelAvatarBuilder.MAX_ACTION_TEXTURE_SIZE (menu icons).</summary>
        const int MaxMenuIconSize = 256;
        const int MaxMenusWalked = 2000;
        const int ParamItemsInline = 64;

        /// <summary>Built-in parameters (docs avatars/animator-parameters/index.md, snapshot 2026-10-20). Not to be added to Expression Parameters.</summary>
        static readonly HashSet<string> BuiltInParameters = new HashSet<string>(StringComparer.Ordinal)
        {
            "IsLocal", "PreviewMode", "Viseme", "Voice", "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight",
            "AngularY", "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude", "Upright", "Grounded", "Seated", "AFK",
            "TrackingType", "VRMode", "MuteSelf", "InStation", "Earmuffs", "IsOnFriendsList", "AvatarVersion", "IsAnimatorEnabled",
            "ScaleModified", "ScaleFactor", "ScaleFactorInverse", "EyeHeightAsMeters", "EyeHeightAsPercent",
        };

        sealed class ParamDef
        {
            public string Name;
            public int Type;
            public bool Saved, Synced;
            public float Default;
        }

        static Dictionary<string, object> AuditExpressions(Ctx ctx)
        {
            var d = new Dictionary<string, object>();
            Object menu, parameters;
            bool menuMissing, paramsMissing, custom;
            using (var so = new SerializedObject(ctx.Descriptor))
            {
                custom = CctSerialized.GetBool(so.FindProperty("customExpressions"));
                var menuProp = so.FindProperty("expressionsMenu");
                var paramsProp = so.FindProperty("expressionParameters");
                menu = CctSerialized.GetObject(menuProp);
                parameters = CctSerialized.GetObject(paramsProp);
                menuMissing = CctSerialized.IsMissingReference(menuProp);
                paramsMissing = CctSerialized.IsMissingReference(paramsProp);
            }
            var descPath = ctx.PathOf(ctx.Descriptor);
            d["custom_expressions"] = custom;
            d["menu_asset"] = menu != null ? AssetPathOrName(menu) : menuMissing ? "(missing)" : null;
            d["parameters_asset"] = parameters != null ? AssetPathOrName(parameters) : paramsMissing ? "(missing)" : null;

            // SDK builder: when either asset is set, both must be set ("VRCExpressionsMenu/Parameters object reference is missing").
            // A reference to a deleted asset is null for the SDK too, so it only blocks when exactly one of the two resolves.
            if ((menu != null) != (parameters != null))
            {
                var absent = menu == null ? "Menu" : "Parameters";
                bool absentIsMissing = menu == null ? menuMissing : paramsMissing;
                ctx.Issue(CctSeverity.Error, "EXPRESSIONS_ASSET_MISSING",
                    $"Expressions {absent} が{(absentIsMissing ? "削除されたアセットを指しています (Missing)" : "未設定です")}。Menu と Parameters の片方だけでは SDK のエラーでアップロードできません。設定し直してください", descPath);
            }
            else if (menuMissing || paramsMissing)
                ctx.Issue(CctSeverity.Warning, "EXPRESSIONS_ASSET_MISSING",
                    $"Expressions {(menuMissing && paramsMissing ? "Menu と Parameters" : menuMissing ? "Menu" : "Parameters")} の参照先アセットが削除されています (Missing)。SDK はエラーにしませんが、Expressions メニューが使えません。設定し直してください", descPath);

            var defs = new List<ParamDef>();
            if (parameters != null) d["parameters"] = AuditParameters(ctx, parameters, defs);
            ctx.ParamDefs = defs;
            var byName = new Dictionary<string, ParamDef>(StringComparer.Ordinal);
            foreach (var p in defs)
                if (!string.IsNullOrEmpty(p.Name) && !byName.ContainsKey(p.Name)) byName[p.Name] = p; // SDK FindParameter: first exact match

            if (menu != null) d["menu"] = AuditMenu(ctx, menu, parameters != null ? byName : null);

            var ma = new Dictionary<string, object>();
            ctx.Scan.TypeCounts.TryGetValue(TMaMenuInstaller, out var installers);
            ctx.Scan.TypeCounts.TryGetValue(TMaMenuItem, out var items);
            ctx.Scan.TypeCounts.TryGetValue(TMaParameters, out var maParams);
            ctx.Scan.TypeCounts.TryGetValue(TMaMergeAnimator, out var merges);
            if (installers + items + maParams + merges > 0)
            {
                ma["menu_installers"] = installers;
                ma["menu_items"] = items;
                ma["parameters_components"] = maParams;
                ma["merge_animators"] = merges;
                ma["note"] = "Modular Avatar はビルド時にメニュー・パラメータ・アニメーターを追加します。ここの数値と検査はビルド前の Descriptor のアセットだけが対象です" +
                             (installers > 0 && (menu == null || !custom) ? "。編集時にメニューが空欄 / customExpressions が OFF でも、MA Menu Installer がビルド時にメニューを生成します (空欄を手で埋める必要はありません)" : "") +
                             " (post_build=true で処理後の値を確認できます)";
                d["modular_avatar"] = ma;
            }
            else if (!custom && (menu != null || parameters != null))
                // MA's "Fixup Expressions Menu" pass (PluginDefinition.cs) sets customExpressions = true on every avatar NDMF builds.
                d["note"] = CctSerialized.FindType(TMaMenuInstaller) != null
                    ? "customExpressions が OFF ですが、Modular Avatar が導入されているため、ビルド時に MA が ON にして割り当て済みのメニュー/パラメータを使います (NDMF の Apply on Build が ON の場合)"
                    : "customExpressions が OFF (Descriptor の Expressions で Customize を押していない状態) なのにメニュー/パラメータが割り当てられています。" +
                      "VRChat 既定の Expressions として扱われ、割り当て済みのアセットは使われない可能性があります (クライアントの挙動は未確認)。使うなら Customize を有効にしてください";
            return d;
        }

        /// <summary>VRCExpressionParameters.parameters through SerializedObject (no SDK call: CalcTotalCost throws on null entries).</summary>
        static void ReadParamDefs(Object asset, List<ParamDef> defs)
        {
            using (var so = new SerializedObject(asset))
            {
                foreach (var e in CctSerialized.Elements(so.FindProperty("parameters")))
                {
                    defs.Add(new ParamDef
                    {
                        Name = CctSerialized.GetString(CctSerialized.Child(e, "name"), ""),
                        Type = CctSerialized.GetInt(CctSerialized.Child(e, "valueType")),
                        Saved = CctSerialized.GetBool(CctSerialized.Child(e, "saved"), true),
                        Synced = CctSerialized.GetBool(CctSerialized.Child(e, "networkSynced"), true),
                        Default = CctSerialized.GetFloat(CctSerialized.Child(e, "defaultValue")),
                    });
                }
            }
        }

        /// <summary>SDK CalcTotalCost: every networkSynced entry (empty names included), Int/Float 8 bits, Bool 1 bit.</summary>
        static int SyncedCost(List<ParamDef> defs) => defs.Where(p => p.Synced).Sum(p => Ranks.ParameterCost(p.Type));

        static Dictionary<string, object> AuditParameters(Ctx ctx, Object asset, List<ParamDef> defs)
        {
            var assetPath = AssetPathOrName(asset);
            ReadParamDefs(asset, defs);
            int cost = SyncedCost(defs);
            int synced = defs.Count(p => p.Synced);
            var d = new Dictionary<string, object>
            {
                ["asset"] = assetPath,
                ["total_bits"] = cost,
                ["limit"] = Ranks.MaxParameterCost,
                ["count"] = defs.Count,
                ["synced"] = synced,
                ["local_only"] = defs.Count - synced,
                ["by_type"] = new Dictionary<string, object>
                {
                    ["int"] = defs.Count(p => p.Type == ParamTypeInt),
                    ["float"] = defs.Count(p => p.Type == ParamTypeFloat),
                    ["bool"] = defs.Count(p => p.Type == ParamTypeBool),
                },
            };
            var rows = defs.Select(p => (object)new Dictionary<string, object>
            {
                ["name"] = p.Name,
                ["type"] = EnumName(ParamTypeNames, p.Type),
                ["saved"] = p.Saved,
                ["synced"] = p.Synced,
                ["default"] = Round(p.Default, 4),
            }).ToList();
            d["items"] = rows.Take(ParamItemsInline).ToList();
            if (rows.Count > ParamItemsInline)
            {
                d["items_omitted"] = rows.Count - ParamItemsInline;
                d["items_note"] = "全件はレポートの expression_parameters にあります";
            }
            ctx.R.Put("expression_parameters", rows);

            if (cost > Ranks.MaxParameterCost)
                ctx.Issue(CctSeverity.Error, "PARAM_BUDGET_EXCEEDED",
                    $"同期パラメータが {cost} bit で上限 {Ranks.MaxParameterCost} bit を超えています (SDK エラーでアップロード不可)。不要なものの Synced を外すか、Int/Float を Bool にまとめてください",
                    asset: assetPath, value: cost, limit: Ranks.MaxParameterCost);
            if (defs.Count > MaxParameterCount)
                ctx.Issue(CctSeverity.Warning, "PARAM_COUNT_EXCEEDED",
                    $"パラメータが {defs.Count} 個あり、docs の上限 {MaxParameterCount} 個を超えています (SDK は検査しません)", asset: assetPath, value: defs.Count, limit: MaxParameterCount);

            foreach (var g in defs.Where(p => !string.IsNullOrEmpty(p.Name)).GroupBy(p => p.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                var types = string.Join("/", g.Select(p => EnumName(ParamTypeNames, p.Type)).Distinct());
                ctx.Issue(CctSeverity.Warning, "PARAM_DUPLICATE",
                    $"パラメータ '{g.Key}' が {g.Count()} 回定義されています ({types})。SDK とメニューは最初の定義だけを使い、同期ビットは全部の分を消費します",
                    asset: assetPath, value: g.Key);
            }
            int emptySynced = defs.Count(p => string.IsNullOrEmpty(p.Name) && p.Synced);
            int emptyLocal = defs.Count(p => string.IsNullOrEmpty(p.Name) && !p.Synced);
            if (emptySynced > 0)
                ctx.Issue(CctSeverity.Warning, "PARAM_EMPTY_NAME",
                    $"名前が空のパラメータが {emptySynced} 個あり、Synced のため同期ビットを消費しています。削除してください", asset: assetPath, value: emptySynced);
            if (emptyLocal > 0)
                ctx.Issue(CctSeverity.Info, "PARAM_EMPTY_NAME", $"名前が空のパラメータが {emptyLocal} 個あります (同期なし)", asset: assetPath, value: emptyLocal);
            foreach (var p in defs.Where(p => BuiltInParameters.Contains(p.Name)))
                ctx.Issue(CctSeverity.Warning, "PARAM_BUILTIN_NAME",
                    $"'{p.Name}' は VRChat の組み込みパラメータ名です。Expression Parameters に追加しないでください (docs)", asset: assetPath, value: p.Name);
            foreach (var p in defs)
            {
                string range = null;
                if (p.Type == ParamTypeInt && (p.Default < 0 || p.Default > 255 || Math.Abs(p.Default - Math.Round(p.Default)) > 1e-6)) range = "Int は 0〜255 の整数";
                else if (p.Type == ParamTypeFloat && (p.Default < -1f || p.Default > 1f)) range = "Float は -1.0〜1.0";
                else if (p.Type == ParamTypeBool && p.Default != 0f && p.Default != 1f) range = "Bool は 0 か 1";
                if (range != null)
                    ctx.Issue(CctSeverity.Info, "PARAM_DEFAULT_OUT_OF_RANGE",
                        $"パラメータ '{p.Name}' の既定値 {p.Default.ToString(CultureInfo.InvariantCulture)} は範囲外です ({range})", asset: assetPath, value: p.Default);
            }
            return d;
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // Menu tree
        // ---------------------------------------------------------------------------------------------------------------------

        static Dictionary<string, object> AuditMenu(Ctx ctx, Object rootMenu, Dictionary<string, ParamDef> parameters)
        {
            var visited = new HashSet<Object>();
            var menus = new List<object>();
            var icons = new Dictionary<Texture2D, string>(); // icon -> first menu path using it
            bool maSplitsMenus = EnabledMaMenuInstallers(ctx) > 0;
            int controlsTotal = 0, maxDepth = 0;
            var queue = new Queue<KeyValuePair<Object, KeyValuePair<string, int>>>();
            queue.Enqueue(new KeyValuePair<Object, KeyValuePair<string, int>>(rootMenu, new KeyValuePair<string, int>(rootMenu.name, 1)));
            visited.Add(rootMenu);
            bool truncated = false;
            while (queue.Count > 0)
            {
                if (visited.Count > MaxMenusWalked) { truncated = true; break; }
                var item = queue.Dequeue();
                var menu = item.Key;
                var menuPath = item.Value.Key;
                int depth = item.Value.Value;
                maxDepth = Math.Max(maxDepth, depth);
                var asset = AssetPathOrName(menu);
                int controls = 0;
                using (var so = new SerializedObject(menu))
                {
                    foreach (var c in CctSerialized.Elements(so.FindProperty("controls")))
                    {
                        controls++;
                        var name = CctSerialized.GetString(CctSerialized.Child(c, "name"), "");
                        int type = CctSerialized.GetInt(CctSerialized.Child(c, "type"));
                        var where = $"{menuPath} > {(string.IsNullOrEmpty(name) ? "(名前なし)" : name)}";

                        AddIcon(icons, CctSerialized.GetObject(CctSerialized.Child(c, "icon")) as Texture2D, where);
                        foreach (var label in CctSerialized.Elements(CctSerialized.Child(c, "labels")))
                            AddIcon(icons, CctSerialized.GetObject(CctSerialized.Child(label, "icon")) as Texture2D, where);

                        var param = CctSerialized.GetString(CctSerialized.Child(c, "parameter.name"), "");
                        CheckMenuParam(ctx, parameters, param, where, asset);

                        var subParams = CctSerialized.Elements(CctSerialized.Child(c, "subParameters"))
                            .Select(sp => CctSerialized.GetString(CctSerialized.Child(sp, "name"), "")).ToList();
                        foreach (var sp in subParams) CheckMenuParam(ctx, parameters, sp, where, asset);
                        int needed = type == ControlTwoAxis ? 2 : type == ControlFourAxis ? 4 : type == ControlRadial ? 1 : 0;
                        if (needed > 0)
                        {
                            if (subParams.Count < needed)
                                ctx.Issue(CctSeverity.Warning, "MENU_PUPPET_SUBPARAM_MISSING",
                                    $"{where}: パペットのサブパラメータが {subParams.Count} 個しかありません (必要数 {needed})", asset: asset);
                            if (parameters != null)
                                foreach (var sp in subParams.Take(needed))
                                    if (!string.IsNullOrEmpty(sp) && parameters.TryGetValue(sp, out var def) && def.Type == ParamTypeBool)
                                        ctx.Issue(CctSeverity.Error, "MENU_PUPPET_BOOL_PARAM",
                                            $"{where}: パペットのサブパラメータ '{sp}' が Bool です (SDK エラー: Int/Float を使ってください)", asset: asset, value: sp);
                        }

                        if (type != ControlSubMenu) continue;
                        var subProp = CctSerialized.Child(c, "subMenu");
                        var sub = CctSerialized.GetObject(subProp);
                        if (sub == null)
                        {
                            ctx.Issue(CctSeverity.Warning, "MENU_SUBMENU_MISSING",
                                $"{where}: Sub Menu にメニューが{(CctSerialized.IsMissingReference(subProp) ? "見つかりません (削除されたアセット)" : "設定されていません")}", asset: asset);
                            continue;
                        }
                        if (!visited.Add(sub))
                        {
                            ctx.Issue(CctSeverity.Info, "MENU_RECURSIVE",
                                $"{where}: サブメニュー '{sub.name}' は既に辿ったメニューです (循環または共有)。SDK と同じく 2 回目は辿りません", asset: asset);
                            continue;
                        }
                        queue.Enqueue(new KeyValuePair<Object, KeyValuePair<string, int>>(sub, new KeyValuePair<string, int>(menuPath + "/" + (string.IsNullOrEmpty(name) ? sub.name : name), depth + 1)));
                    }
                }
                controlsTotal += controls;
                menus.Add(new Dictionary<string, object> { ["path"] = menuPath, ["asset"] = asset, ["depth"] = depth, ["controls"] = controls });
                if (controls > MaxMenuControls)
                {
                    // MA MenuInstallHook (enabled Menu Installer present) re-serializes the whole menu tree at build time and splits every
                    // menu with more than 8 controls into 7 + a "More" sub menu (modular-avatar Editor/MenuInstallHook.cs SplitMenu).
                    if (maSplitsMenus)
                        ctx.Issue(CctSeverity.Info, "MENU_TOO_MANY_CONTROLS",
                            $"メニュー '{menuPath}' のコントロールが {controls} 個あり、上限 {MaxMenuControls} を超えています。有効な MA Menu Installer があるため、ビルド時に MA が 7 個 + \"More\" サブメニューに分割します (並びが変わってよいか確認してください)",
                            asset: asset, value: controls, limit: MaxMenuControls);
                    else
                        ctx.Issue(CctSeverity.Warning, "MENU_TOO_MANY_CONTROLS",
                            $"メニュー '{menuPath}' のコントロールが {controls} 個あり、1 メニュー {MaxMenuControls} 個の上限 (docs) を超えています。SDK のアップロード検査は止めませんが、ゲーム内で正しく表示される保証はありません。サブメニューに分けてください",
                            asset: asset, value: controls, limit: MaxMenuControls);
                }
            }

            // SDK: icons > 256 px whose importer max size is > 256, or uncompressed, are an upload error ("Images used for Actions & Moods are too large").
            var badIcons = new List<string>();
            foreach (var kv in icons)
            {
                var tex = kv.Key;
                var path = AssetDatabase.GetAssetPath(tex);
                var importer = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;
                var settings = importer.GetDefaultPlatformTextureSettings();
                bool tooLarge = (tex.width > MaxMenuIconSize || tex.height > MaxMenuIconSize) && settings.maxTextureSize > MaxMenuIconSize;
                bool uncompressed = settings.textureCompression == TextureImporterCompression.Uncompressed;
                if (tooLarge || uncompressed)
                    badIcons.Add($"{path} ({tex.width}x{tex.height}{(tooLarge ? ", Max Size " + settings.maxTextureSize : "")}{(uncompressed ? ", 非圧縮" : "")})");
            }
            if (badIcons.Count > 0)
                ctx.Issue(CctSeverity.Error, "MENU_ICON_INVALID",
                    $"メニューのアイコン {badIcons.Count} 枚が SDK の条件 (256px 以下または Max Size 256 以下、かつ圧縮) を満たしていません。アップロードできません: {string.Join(", ", badIcons.Take(5))}{(badIcons.Count > 5 ? " ほか" : "")}",
                    value: badIcons.Count, limit: MaxMenuIconSize);

            var d = new Dictionary<string, object>
            {
                ["asset"] = AssetPathOrName(rootMenu),
                ["menus"] = menus.Count,
                ["controls_total"] = controlsTotal,
                ["max_depth"] = maxDepth,
                ["max_controls_per_menu"] = MaxMenuControls,
                ["icons"] = icons.Count,
                ["tree"] = menus.Take(64).ToList(),
            };
            if (menus.Count > 64) d["tree_omitted"] = menus.Count - 64;
            if (truncated) d["truncated"] = true;
            return d;
        }

        /// <summary>MA Menu Installers that MA's MenuInstallHook processes: enabled components outside EditorOnly objects.</summary>
        static int EnabledMaMenuInstallers(Ctx ctx)
        {
            int n = 0;
            foreach (var c in ctx.Scan.Components)
                if (c is Behaviour b && b.enabled && !ctx.IsEditorOnly(c) && CctSerialized.IsType(c, TMaMenuInstaller)) n++;
            return n;
        }

        static void AddIcon(Dictionary<Texture2D, string> icons, Texture2D icon, string where)
        {
            if (icon != null && !icons.ContainsKey(icon)) icons[icon] = where;
        }

        /// <summary>SDK: a non-empty control (sub-)parameter must exist in Expression Parameters (exact name; built-ins are not accepted either).</summary>
        static void CheckMenuParam(Ctx ctx, Dictionary<string, ParamDef> parameters, string name, string where, string asset)
        {
            if (parameters == null || string.IsNullOrEmpty(name) || parameters.ContainsKey(name)) return;
            ctx.Issue(CctSeverity.Error, "MENU_PARAM_UNDEFINED",
                $"{where}: パラメータ '{name}' が Expression Parameters にありません (SDK エラー{(BuiltInParameters.Contains(name) ? "。組み込みパラメータもメニューからは使えません" : "")})",
                asset: asset, value: name);
        }
    }
}
