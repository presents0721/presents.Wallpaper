using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace presents.Wallpaper;

/// <summary>
/// Wallpaper Engine 用户属性（project.json 的 general.properties）。
/// 面板按这份 schema 生成控件，改完直接把值推给正在跑的场景，不重挂 pkg。
/// </summary>
internal static class WallpaperProps
{
    internal sealed class Option
    {
        public string Label = "";
        public JsonElement? Value;
    }

    internal sealed class Prop
    {
        public string Name = "";
        public string Kind = "other";
        public string Text = "";
        public double Order;
        public JsonElement? Value;
        public bool Overridden;
        public bool Hidden;
        public bool Locked;
        public string? Condition;
        public string? FileType;
        public double? Min, Max, Step, Precision;
        public List<Option>? Options;
        public List<string>? Files;
    }

    static readonly object Gate = new();
    static readonly Dictionary<string, (DateTime Stamp, List<Prop> Defs)> Cache = new();

    static readonly string[] LangOrder = { "zh-chs", "zh-cht", "en-us" };

    /// <summary>WE 自己的元键，工坊里直接当文案用；不翻译会露出 ui_browse_* 这种原文。</summary>
    static readonly Dictionary<string, string> TextAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ui_browse_properties_scheme_color"] = "配色",
    };

    static readonly HashSet<string> Known =
        new(StringComparer.OrdinalIgnoreCase) { "color", "bool", "slider", "combo", "text", "textinput", "file", "directory", "group", "scenetexture", "texture" };

    static readonly Dictionary<string, string[]> FileExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image"] = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".apng", ".svg" },
        ["video"] = new[] { ".mp4", ".webm", ".mov", ".mkv", ".avi", ".m4v" },
        ["audio"] = new[] { ".mp3", ".ogg", ".oga", ".wav", ".m4a", ".flac", ".aac" },
    };

    // ---------------- 解析 ----------------

    /// <summary>读出这张壁纸声明的属性（含用户覆盖值与条件可见性）。</summary>
    internal static List<Prop> Resolve(WallpaperItem item, Dictionary<string, string>? overrides)
    {
        List<Prop> defs = ReadDefs(item);
        if (defs.Count == 0) return defs;

        var map = new Dictionary<string, Prop>(StringComparer.OrdinalIgnoreCase);
        foreach (Prop p in defs) map[p.Name] = p;

        List<Prop> list = defs.Select(Clone).ToList();
        foreach (Prop p in list)
        {
            if (overrides != null && overrides.TryGetValue(p.Name, out string? raw) && TryParse(raw, out JsonElement value))
            {
                p.Value = value;
                p.Overridden = true;
            }

        }

        // 让同一张表里的引用拿到“合并后”的值，条件判断才对得上
        foreach (Prop p in list) map[p.Name] = p;
        foreach (Prop p in list) p.Hidden = p.Locked || !EvalCondition(p.Condition, map);

        return list;
    }

    /// <summary>只下发用户改过的值：没改过的留给壁纸自己的默认值（scene.json 里的可能和 project.json 不同）。</summary>
    internal static Dictionary<string, object?> Overrides(List<Prop> props)
    {
        var map = new Dictionary<string, object?>();
        foreach (Prop p in props)
        {
            if (!p.Overridden || p.Value == null) continue;
            map[p.Name] = ToWire(p.Kind, p.Value.Value);
        }
        return map;
    }

    /// <summary>面板用的 JSON 模型。</summary>
    internal static List<object> ForPanel(List<Prop> props)
    {
        var list = new List<object>();

        foreach (Prop p in props)
        {
            if (p.Kind == "text" || p.Kind == "group")
            {
                if (!string.IsNullOrWhiteSpace(p.Text)) list.Add(new { name = p.Name, kind = "title", text = p.Text, hidden = p.Hidden });
                continue;
            }

            list.Add(new
            {
                name = p.Name,
                kind = p.Kind,
                text = p.Text,
                value = p.Value == null ? null : ToWire(p.Kind, p.Value.Value),
                min = p.Min,
                max = p.Max,
                step = p.Step ?? (p.Kind == "slider"
                    ? (p.Precision != null ? Math.Pow(10, -p.Precision.Value) : 0.01)
                    : (double?)null),
                hidden = p.Hidden,
                overridden = p.Overridden,
                options = p.Options?.Select(o => new
                {
                    label = o.Label,
                    value = o.Value == null ? null : ToWire(p.Kind, o.Value.Value)
                }).ToList(),
                files = p.Files
            });
        }

        return list;
    }

    // ---------------- 值转换 ----------------

    /// <summary>按名字找一条属性定义（忽略大小写，WE 的属性名大小写不统一）。</summary>
    internal static Prop? Find(List<Prop> props, string name)
    {
        foreach (Prop p in props)
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>取属性当前值（已转成引擎要的标量），用于「还原默认值」时把作者默认值推给场景。</summary>
    internal static object? Wire(Prop p) => p.Value == null ? null : ToWire(p.Kind, p.Value.Value);

    /// <summary>把挂件传来的 JSON 文本按属性类型转成引擎要的标量。</summary>
    internal static bool TryWire(string raw, string kind, out object? value)
    {
        value = null;
        if (!TryParse(raw, out JsonElement parsed)) return false;

        value = ToWire(kind, parsed);
        return true;
    }

    /// <summary>
    /// 引擎内部认的属性节点形状是 WE 的 { value, type }，不是裸标量。
    /// 只给标量时 Ig() 里的 ik() 因为拿不到 type 不会把属性登记进场景属性表，
    /// 之后所有图层绑定（visible / color / origin …）都解析成 null、退回作者默认值 ——
    /// 表现就是「拖尾颜色、各种开关、组件位置全都不对」。所以下发一律用节点。
    /// </summary>
    internal static Dictionary<string, object?> Node(Prop p) => new()
    {
        ["value"] = p.Value == null ? null : ToWire(p.Kind, p.Value.Value),
        ["type"] = NativeType(p.Kind)
    };

    /// <summary>用户改过的那些属性 → 引擎节点表（原生通道一次下发的形状）。</summary>
    internal static Dictionary<string, object?> NativeNodes(List<Prop> props)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (Prop p in props)
            if (p.Overridden && p.Value != null) map[p.Name] = Node(p);

        return map;
    }

    /// <summary>同 Node，但值来自挂件传来的 JSON 文本。</summary>
    internal static bool TryNode(string raw, string kind, out Dictionary<string, object?>? node)
    {
        node = null;
        if (!TryParse(raw, out JsonElement parsed)) return false;

        node = new Dictionary<string, object?>
        {
            ["value"] = ToWire(kind, parsed),
            ["type"] = NativeType(kind)
        };

        return true;
    }

    /// <summary>我们的类型名 → 引擎属性表认的类型名（slider / color / bool / combo / textinput）。</summary>
    static string NativeType(string kind) => kind switch
    {
        "slider" => "slider",
        "color" => "color",
        "bool" => "bool",
        "combo" => "combo",
        _ => "textinput"
    };

    /// <summary>project.json 的值 → 引擎要的标量（bool / number / string，color 保持 "r g b"）。</summary>
    static object? ToWire(string kind, JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number: return v.TryGetDouble(out double d) ? d : 0d;
            case JsonValueKind.String: return v.GetString();
            case JsonValueKind.Null: return null;
            case JsonValueKind.Undefined: return null;
            default: return v.GetRawText();
        }
    }

    static bool TryParse(string raw, out JsonElement value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            value = doc.RootElement.Clone();
            return true;
        }
        catch { return false; }
    }

    static Prop Clone(Prop p) => new()
    {
        Name = p.Name,
        Kind = p.Kind,
        Text = p.Text,
        Order = p.Order,
        Value = p.Value,
        Overridden = false,
        Hidden = p.Locked,
        Locked = p.Locked,
        Condition = p.Condition,
        FileType = p.FileType,
        Min = p.Min,
        Max = p.Max,
        Step = p.Step,
        Precision = p.Precision,
        Options = p.Options,
        Files = p.Files
    };

    // ---------------- project.json ----------------

    static List<Prop> ReadDefs(WallpaperItem item)
    {
        string file;
        try { file = Path.Combine(item.Root, "project.json"); } catch { return new List<Prop>(); }
        if (!File.Exists(file)) return new List<Prop>();

        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(file); } catch { return new List<Prop>(); }

        lock (Gate)
        {
            if (Cache.TryGetValue(file, out (DateTime Stamp, List<Prop> Defs) hit) && hit.Stamp == stamp)
                return hit.Defs;
        }

        List<Prop> defs;
        try
        {
            using FileStream fs = File.OpenRead(file);
            using JsonDocument doc = JsonDocument.Parse(fs);

            if (!doc.RootElement.TryGetProperty("general", out JsonElement general)) return new List<Prop>();
            if (!general.TryGetProperty("properties", out JsonElement properties) || properties.ValueKind != JsonValueKind.Object)
                return new List<Prop>();

            JsonElement? localization = general.TryGetProperty("localization", out JsonElement loc) ? loc : null;

            defs = new List<Prop>();
            foreach (JsonProperty entry in properties.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                if (!entry.Value.TryGetProperty("type", out JsonElement typeEl) || typeEl.ValueKind != JsonValueKind.String) continue;

                // 作者标记「用户不可编辑」：面板不显示，但默认值仍由壁纸自己持有
                bool editable = !entry.Value.TryGetProperty("editable", out JsonElement ed) || ed.ValueKind != JsonValueKind.False;

                string rawKind = typeEl.GetString() ?? "other";
                string kind = Known.Contains(rawKind) ? rawKind.ToLowerInvariant() : "other";

                // 纹理选择在 WE 里就是「从项目里挑一个文件」，UI 和 file 一样
                if (kind is "scenetexture" or "texture") kind = "file";

                // fraction: false = 只能取整数（barcount 这类），WE 默认 true
                bool fraction = !entry.Value.TryGetProperty("fraction", out JsonElement fr) || fr.ValueKind != JsonValueKind.False;

                var p = new Prop
                {
                    Name = entry.Name,
                    Kind = kind,
                    Text = Text(localization, entry.Value, entry.Name),
                    Order = Number(entry.Value, "order") ?? 0,
                    Condition = Str(entry.Value, "condition"),
                    FileType = Str(entry.Value, "fileType"),
                    Min = Number(entry.Value, "min"),
                    Max = Number(entry.Value, "max"),
                    Step = Number(entry.Value, "step") ?? (fraction ? null : 1),
                    Precision = Number(entry.Value, "precision"),
                };

                if (entry.Value.TryGetProperty("value", out JsonElement v) && v.ValueKind != JsonValueKind.Null)
                    p.Value = v.Clone();
                else if (kind != "text" && kind != "group")
                    p.Value = DefaultFor(kind);

                if (entry.Value.TryGetProperty("options", out JsonElement options) && options.ValueKind == JsonValueKind.Array)
                {
                    p.Options = new List<Option>();
                    foreach (JsonElement o in options.EnumerateArray())
                    {
                        if (o.ValueKind != JsonValueKind.Object) continue;
                        if (!o.TryGetProperty("label", out JsonElement label) || label.ValueKind != JsonValueKind.String) continue;
                        if (!o.TryGetProperty("value", out JsonElement ov)) continue;

                        p.Options.Add(new Option
                        {
                            Label = Localize(localization, label.GetString() ?? "") ?? Clean(label.GetString() ?? ""),
                            Value = ov.Clone()
                        });
                    }
                    if (p.Options.Count == 0) p.Options = null;
                }

                if (kind == "file" && p.FileType == null &&
                    (rawKind.Equals("scenetexture", StringComparison.OrdinalIgnoreCase) || rawKind.Equals("texture", StringComparison.OrdinalIgnoreCase)))
                    p.FileType = "image";

                if (kind is "file" or "directory") p.Files = ListFiles(item, p.FileType);

                // 作者标记「用户不可编辑」：面板不显示，但默认值仍由壁纸自己持有
                // schemecolor 是 WE 自己的元属性，WE 面板里也不给用户看
                p.Locked = !editable || entry.Name.Equals("schemecolor", StringComparison.OrdinalIgnoreCase);
                p.Hidden = p.Locked;

                defs.Add(p);
            }

            defs.Sort((a, b) =>
            {
                int c = a.Order.CompareTo(b.Order);
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
        }
        catch { return new List<Prop>(); }

        lock (Gate) Cache[file] = (stamp, defs);
        return defs;
    }

    static List<string>? ListFiles(WallpaperItem item, string? fileType)
    {
        try
        {
            string[]? exts = fileType != null && FileExt.TryGetValue(fileType, out string[]? known) ? known : null;
            var list = new List<string>();

            foreach (string full in Directory.EnumerateFiles(item.Root, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(item.Root, full).Replace('\\', '/');
                if (rel.Count(c => c == '/') > 2) continue;              // 太深的不列，免得下拉框爆掉
                if (exts != null && !exts.Any(e => rel.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;

                list.Add(rel);
                if (list.Count >= 120) break;
            }

            return list.Count > 0 ? list : null;
        }
        catch { return null; }
    }

    static JsonElement DefaultFor(string kind)
    {
        string raw = kind switch
        {
            "bool" => "false",
            "slider" => "0",
            _ => "\"\""
        };
        using JsonDocument doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    static string? Str(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.String) return null;
        string s = (el.GetString() ?? "").Trim();
        return s.Length == 0 ? null : s;
    }

    static double? Number(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d)) return d;
        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s)) return s;
        return null;
    }

    // ---------------- 文案 ----------------

    static string Text(JsonElement? localization, JsonElement def, string fallback)
    {
        string raw = def.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "") : "";
        string? loc = Localize(localization, raw);
        string clean = Clean(loc ?? raw);

        if (TextAlias.TryGetValue(clean, out string? alias)) return alias;

        return clean.Length == 0 ? fallback : clean;
    }

    static string? Localize(JsonElement? localization, string key)
    {
        if (localization == null || localization.Value.ValueKind != JsonValueKind.Object || key.Length == 0) return null;

        foreach (string lang in LangOrder)
        {
            foreach (JsonProperty table in localization.Value.EnumerateObject())
            {
                if (!table.Name.Equals(lang, StringComparison.OrdinalIgnoreCase)) continue;
                if (table.Value.ValueKind != JsonValueKind.Object) continue;
                if (table.Value.TryGetProperty(key, out JsonElement text) && text.ValueKind == JsonValueKind.String)
                {
                    string s = text.GetString() ?? "";
                    if (s.Trim().Length > 0) return s;
                }
            }
        }

        return null;
    }

    static string Clean(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        string s = Regex.Replace(raw, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]*>", "");
        s = s.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<")
             .Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'");
        s = Regex.Replace(s, "[ \\t]+\\n", "\n");

        return s.Trim();
    }

    // ---------------- 条件 ----------------

    /// <summary>
    /// WE 的 condition 是迷你布尔表达式，真实样本里长这样：
    ///   bar_settings.value
    ///   background.value ==1 &amp;&amp; background_settings.value
    /// 只支持 &amp;&amp; / || / 比较运算符 + name[.value] 引用，够覆盖工坊里的常见写法。
    /// </summary>
    static bool EvalCondition(string? expr, Dictionary<string, Prop> map)
    {
        if (string.IsNullOrWhiteSpace(expr)) return true;

        foreach (string orPart in expr.Split("||"))
        {
            bool all = true;
            foreach (string andPart in orPart.Split("&&"))
            {
                if (!EvalTerm(andPart.Trim(), map)) { all = false; break; }
            }
            if (all) return true;
        }

        return false;
    }

    static bool EvalTerm(string term, Dictionary<string, Prop> map)
    {
        if (term.Length == 0) return true;

        Match m = Regex.Match(term, @"^([A-Za-z0-9_.\-]+)\s*(==|!=|>=|<=|>|<)\s*(.+)$");
        if (m.Success)
        {
            string left = Scalar(map, m.Groups[1].Value);
            string right = m.Groups[3].Value.Trim().Trim('"', '\'');

            if (double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) &&
                double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
            {
                return m.Groups[2].Value switch
                {
                    "==" => Math.Abs(a - b) < 1e-6,
                    "!=" => Math.Abs(a - b) >= 1e-6,
                    ">" => a > b,
                    "<" => a < b,
                    ">=" => a >= b,
                    "<=" => a <= b,
                    _ => true
                };
            }

            int cmp = string.CompareOrdinal(left, right);
            return m.Groups[2].Value switch
            {
                "==" => cmp == 0,
                "!=" => cmp != 0,
                ">" => cmp > 0,
                "<" => cmp < 0,
                ">=" => cmp >= 0,
                "<=" => cmp <= 0,
                _ => true
            };
        }

        return Truthy(Scalar(map, term));
    }

    static string Scalar(Dictionary<string, Prop> map, string reference)
    {
        string name = reference.Trim();
        if (name.EndsWith(".value", StringComparison.OrdinalIgnoreCase)) name = name[..^6];

        if (!map.TryGetValue(name, out Prop? p) || p.Value == null) return "";
        return p.Value.Value.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => p.Value.Value.GetRawText(),
            JsonValueKind.String => p.Value.Value.GetString() ?? "",
            _ => ""
        };
    }

    // ---------------- 用户覆盖值持久化 ----------------

    /// <summary>
    /// 用户改过的属性值，按壁纸 ID 存在插件目录的 user-props.json 里。
    /// 值统一存 JSON 文本（true / 1.5 / "abc"），取用时按类型的原样还原。
    /// </summary>
    internal static class Store
    {
        static readonly object Gate = new();
        static Dictionary<string, Dictionary<string, string>> data = new(StringComparer.OrdinalIgnoreCase);
        static string? path;
        static Timer? saveTimer;

        internal static void Init(string pluginDirectory)
        {
            try
            {
                path = Path.Combine(pluginDirectory, "user-props.json");
                if (!File.Exists(path)) return;

                var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path));
                if (loaded != null) data = new Dictionary<string, Dictionary<string, string>>(loaded, StringComparer.OrdinalIgnoreCase);

                // 每张壁纸内部也统一成大小写不敏感，属性名按 WE 的原样匹配
                foreach (string key in data.Keys.ToList())
                    data[key] = new Dictionary<string, string>(data[key], StringComparer.OrdinalIgnoreCase);
            }
            catch { }
        }

        internal static Dictionary<string, string>? Get(string id)
        {
            lock (Gate) return data.TryGetValue(id, out Dictionary<string, string>? hit) ? new Dictionary<string, string>(hit, StringComparer.OrdinalIgnoreCase) : null;
        }

        internal static void Set(string id, string name, string jsonValue)
        {
            lock (Gate)
            {
                if (!data.TryGetValue(id, out Dictionary<string, string>? one))
                    data[id] = one = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                one[name] = jsonValue;
            }

            SaveSoon();
        }

        /// <summary>删掉一条覆盖值：下次读到的是作者默认值（单个属性「还原」）。</summary>
        internal static void Remove(string id, string name)
        {
            lock (Gate)
            {
                if (!data.TryGetValue(id, out Dictionary<string, string>? one)) return;
                one.Remove(name);
                if (one.Count == 0) data.Remove(id);
            }

            SaveSoon();
        }

        internal static void Clear(string id)
        {
            lock (Gate) data.Remove(id);
            SaveSoon();
        }

        /// <summary>拖动滑条会连着来几十次，攒 500ms 写一次盘就够了。</summary>
        static void SaveSoon()
        {
            if (path == null) return;

            lock (Gate)
            {
                saveTimer?.Dispose();
                saveTimer = new Timer(_ => Flush(), null, 500, Timeout.Infinite);
            }
        }

        static void Flush()
        {
            string target;
            string json;

            lock (Gate)
            {
                if (path == null) return;
                target = path;
                try { json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }); }
                catch { return; }
            }

            try { File.WriteAllText(target, json); } catch { }
        }
    }

    static bool Truthy(string raw)
    {
        if (raw.Length == 0) return false;
        if (raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        if (raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return Math.Abs(n) > 1e-6;
        return true;
    }
}
