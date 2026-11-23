using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace presents.Wallpaper;

/// <summary>
/// 扫描本机 Wallpaper Engine 壁纸库：
///   1) Steam 创意工坊 431960（订阅的壁纸）；
///   2) Wallpaper Engine 本地项目 myprojects / defaultprojects；
///   3) 用户配置的额外目录。
/// 只读取 project.json，不启动 Wallpaper Engine，也不修改任何文件。
/// </summary>
internal sealed class WallpaperCatalog
{
    readonly List<WallpaperItem> items = new();

    public int Count => items.Count;
    public IReadOnlyList<WallpaperItem> Items => items;

    /// <summary>
    /// 过滤（跳过场景 / 排除关键词）之前扫到的原始数量。
    /// 挂件拿它和列表的实际条数做对比，数量对不上时用户一眼能看出是被过滤掉了而不是没扫到。
    /// </summary>
    public int ScannedCount { get; private set; }

    /// <summary>扫描到但还没过滤时，自己的素材 / 引擎壁纸各有多少。</summary>
    public int ScannedMediaCount { get; private set; }

    /// <summary>扫描到但还没过滤时，引擎壁纸（创意工坊 / 项目 / 额外目录）有多少。</summary>
    public int ScannedEngineCount { get; private set; }

    /// <summary>最近一次扫描的可读摘要，便于排查“为什么是 0 张”。</summary>
    public string LastSummary { get; private set; } = "";

    /// <summary>
    /// 从列表里精确摘掉一条（删除到回收站后用）。
    /// 不走整库重扫：大库重扫要几秒，而且容易把之前删掉的又扫回来。
    /// </summary>
    public bool Remove(WallpaperItem item)
        => items.RemoveAll(x => string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>上次扫描时间：挂件轮询时用它决定要不要重扫（用户刚丢进去的图要能自己出来）。</summary>
    public DateTime LastScanUtc { get; private set; } = DateTime.MinValue;

    public void Refresh(WallpaperConfig config)
    {
        LastScanUtc = DateTime.UtcNow;

        var found = new Dictionary<string, WallpaperItem>(StringComparer.OrdinalIgnoreCase);

        foreach (string library in SteamLibraries())
        {
            Scan(Path.Combine(library, "steamapps", "workshop", "content", "431960"), 1, "workshop", found);

            string projects = Path.Combine(library, "steamapps", "common", "wallpaper_engine", "projects");
            Scan(Path.Combine(projects, "myprojects"), 1, "project", found);
            Scan(Path.Combine(projects, "defaultprojects"), 1, "project", found);
        }

        foreach (string extra in SplitDirectories(config.ExtraDirectories))
            Scan(extra, 2, "custom", found);

        // 用户自己的图片 / 视频：不依赖 Wallpaper Engine，放在图片目录里就能选
        ScanMedia(config, found);

        string[] excludes = SplitKeywords(config.ExcludeKeywords);

        ScannedCount = found.Count;

        // 「我的」标签要拿自己的扫描基数做对比，不能跟引擎壁纸混在一起算
        ScannedMediaCount = found.Values.Count(x => x.IsUserMedia);
        ScannedEngineCount = ScannedCount - ScannedMediaCount;

        items.Clear();
        foreach (WallpaperItem item in found.Values.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            if (config.SkipScenes && !item.IsPlayable) continue;
            if (excludes.Any(keyword => item.Title.Contains(keyword, StringComparison.CurrentCultureIgnoreCase))) continue;
            items.Add(item);
        }

        int workshop = items.Count(x => x.Source == "workshop");
        int project = items.Count(x => x.Source == "project");
        int custom = items.Count(x => x.Source == "custom");
        int media = items.Count(x => x.IsUserMedia);
        LastSummary = $"共 {items.Count} 张（创意工坊 {workshop}，本地项目 {project}，自定义项目 {custom}，自己的图片/视频 {media}）";
    }

    public WallpaperItem? Find(string? query)
    {
        if (items.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(query)) return items.FirstOrDefault(x => x.IsPlayable) ?? items[0];

        query = query.Trim();
        WallpaperItem? byId = items.FirstOrDefault(x => x.Id.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (byId != null) return byId;

        WallpaperItem? exact = items.FirstOrDefault(x => x.Title.Equals(query, StringComparison.CurrentCultureIgnoreCase));
        if (exact != null) return exact;

        return items.FirstOrDefault(x => x.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    public WallpaperItem? FindByTag(string? tag)
    {
        if (items.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(tag)) return items.FirstOrDefault(x => x.IsPlayable) ?? items[0];

        tag = tag.Trim();

        WallpaperItem? byTag = items.FirstOrDefault(x =>
            x.Tags.Any(t => t.Contains(tag, StringComparison.CurrentCultureIgnoreCase)));
        if (byTag != null) return byTag;

        return items.FirstOrDefault(x => x.Title.Contains(tag, StringComparison.CurrentCultureIgnoreCase));
    }

    public WallpaperItem? Step(int currentIndex, int delta)
    {
        if (items.Count == 0) return null;
        int n = items.Count;

        if (currentIndex < 0)
            return delta >= 0 ? items[0] : items[n - 1];

        int i = (currentIndex + delta) % n;
        if (i < 0) i += n;
        return items[i];
    }

    public int IndexOf(WallpaperItem item) => items.IndexOf(item);

    // ---------------- 内部实现 ----------------

    static void Scan(string root, int maxDepth, string source, Dictionary<string, WallpaperItem> found)
    {
        if (!Directory.Exists(root)) return;

        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            (string directory, int depth) = queue.Dequeue();
            try
            {
                string projectJson = Path.Combine(directory, "project.json");
                if (File.Exists(projectJson))
                {
                    WallpaperItem? item = Parse(projectJson, source);
                    if (item != null) found.TryAdd(item.Root, item);
                    continue;   // 项目目录不再继续向下钻取
                }

                if (depth >= maxDepth) continue;
                foreach (string sub in Directory.EnumerateDirectories(directory))
                    queue.Enqueue((sub, depth + 1));
            }
            catch
            {
                // 无权限 / 目录消失：跳过
            }
        }
    }

    static WallpaperItem? Parse(string projectJson, string source)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(projectJson));
            JsonElement root = document.RootElement;
            string projectRoot = Path.GetDirectoryName(projectJson)!;

            string title = GetString(root, "title") ?? new DirectoryInfo(projectRoot).Name;
            string type = (GetString(root, "type") ?? "").Trim().ToLowerInvariant();
            if (type == "application") return null;   // 需要启动外部程序，明确不支持

            string file = NormalizeRelative(projectRoot, GetString(root, "file") ?? "");
            string preview = NormalizeRelative(projectRoot, GetString(root, "preview") ?? "");

            // 有项目不写 type（或写空）：按实际资源推断，否则视频壁纸会被当成场景壁纸，只能显示一张糊掉的封面
            if (type.Length == 0)
            {
                if (file.Length > 0 && IsVideoFile(file)) type = "video";
                else if (file.Length > 0 && file.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) type = "web";
                else
                {
                    string? video = FindVideoFile(projectRoot);
                    if (video != null) { type = "video"; file = video; }
                    else type = "scene";
                }
            }

            // 部分项目没写 preview 字段，按常见文件名回退
            if (preview.Length == 0)
            {
                foreach (string candidate in new[] { "preview.jpg", "preview.png", "preview.gif", "preview.webp" })
                {
                    string relative = NormalizeRelative(projectRoot, candidate);
                    if (relative.Length > 0 && File.Exists(Path.Combine(projectRoot, relative)))
                    {
                        preview = relative;
                        break;
                    }
                }
            }

            if (type == "video" && file.Length == 0) return null;
            if (type == "web" && file.Length == 0) file = "index.html";

            string id = "wp_" + Hash(projectRoot);
            return new WallpaperItem(id, title.Trim(), type, projectRoot, file, preview, source)
            {
                Tags = ParseTags(root)
            };
        }
        catch
        {
            return null;
        }
    }

    static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    static IReadOnlyList<string> ParseTags(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("tags", out JsonElement tags) ||
            tags.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var result = new List<string>();
        foreach (JsonElement tag in tags.EnumerateArray())
        {
            if (tag.ValueKind != JsonValueKind.String) continue;
            string? value = tag.GetString();
            if (!string.IsNullOrWhiteSpace(value)) result.Add(value.Trim());
        }
        return result;
    }

    /// <summary>把 project.json 里的相对路径规范化为 / 分隔；越界路径直接判空。</summary>
    static string NormalizeRelative(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return "";
        relative = relative.Replace('\\', '/').TrimStart('/');

        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))); }
        catch { return ""; }

        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? relative : "";
    }

    static string Hash(string text)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).Substring(0, 12).ToLowerInvariant();

    static bool IsVideoFile(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>项目没写 type 时，找项目里第一个视频文件（深度受限，避免扫大目录）。</summary>
    static string? FindVideoFile(string root)
    {
        try
        {
            var queue = new Queue<(string Directory, int Depth)>();
            queue.Enqueue((root, 0));

            while (queue.Count > 0)
            {
                (string directory, int depth) = queue.Dequeue();

                foreach (string candidate in Directory.EnumerateFiles(directory))
                    if (IsVideoFile(candidate)) return Path.GetRelativePath(root, candidate).Replace('\\', '/');

                if (depth >= 2) continue;
                foreach (string sub in Directory.EnumerateDirectories(directory))
                    queue.Enqueue((sub, depth + 1));
            }
        }
        catch
        {
            // 无权限 / 目录消失：跳过
        }

        return null;
    }

    // ---------------- 用户自己的图片 / 视频 ----------------

    static readonly string[] ImageExtensions =
        { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".avif" };

    static readonly string[] VideoExtensions =
        { ".mp4", ".webm", ".m4v", ".mov" };

    static readonly HashSet<string> JunkDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN", "System Volume Information", "node_modules", ".git", "AppData",
        "Windows", "WindowsApps", "ProgramData", ".cache", "CacheStorage", "Cache",
    };

    /// <summary>
    /// 递归扫用户自己的图片 / 视频，直接变成可播放壁纸。
    /// 不读 project.json、不依赖 Wallpaper Engine，用来兜住"没有 WE 也想换壁纸"。
    /// </summary>
    static void ScanMedia(WallpaperConfig config, Dictionary<string, WallpaperItem> found)
    {
        int depth = Math.Clamp(config.MediaDepth <= 0 ? 3 : config.MediaDepth, 1, 6);
        int budget = Math.Clamp(config.MediaLimit <= 0 ? 200 : config.MediaLimit, 10, 2000);

        var titles = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (WallpaperItem existing in found.Values) titles.Add(existing.Title);

        foreach ((string root, int rootDepth) in MediaRoots(config, depth))
        {
            if (budget <= 0) break;

            var candidates = new List<(string Path, string Type, long Size)>();
            CollectMedia(root, rootDepth, candidates);

            // 大的更有可能是壁纸：图标、缩略图、聊天表情会被预算先截掉
            foreach ((string path, string type, long _) in candidates.OrderByDescending(x => x.Size))
            {
                if (budget <= 0) break;
                if (!TryAddMedia(path, type, titles, found)) continue;
                budget--;
            }
        }
    }

    /// <summary>把一个根目录下的候选文件收集起来（按大小排序前先不截断，交给调用方分配预算）。</summary>
    static void CollectMedia(string root, int maxDepth, List<(string Path, string Type, long Size)> sink)
    {
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            (string directory, int level) = queue.Dequeue();

            string[] files;
            try { files = Directory.GetFiles(directory); }
            catch { continue; }

            foreach (string file in files)
            {
                string type = MediaType(file);
                if (type.Length == 0) continue;

                try
                {
                    var info = new FileInfo(file);
                    if (info.Length < 40 * 1024) continue;   // 图标 / 缩略图 / 表情包，不是壁纸
                    sink.Add((file, type, info.Length));
                }
                catch
                {
                    // 文件被占用 / 刚被删，跳过
                }
            }

            if (level >= maxDepth) continue;

            try
            {
                foreach (string sub in Directory.GetDirectories(directory))
                {
                    string name = Path.GetFileName(sub);
                    if (name.Length == 0 || name[0] == '.') continue;
                    if (JunkDirectories.Contains(name)) continue;
                    queue.Enqueue((sub, level + 1));
                }
            }
            catch
            {
                // 无权限的子目录跳过
            }
        }
    }

    static string MediaType(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ImageExtensions.Contains(ext)) return "image";
        if (VideoExtensions.Contains(ext)) return "video";
        return "";
    }

    static bool TryAddMedia(string file, string type, HashSet<string> titles, Dictionary<string, WallpaperItem> found)
    {
        try
        {
            string directory = Path.GetDirectoryName(file)!;
            string name = Path.GetFileName(file);
            string title = Path.GetFileNameWithoutExtension(file).Trim();
            if (title.Length == 0) return false;

            // 同名文件很常见（1.jpg、封面.png），补上父目录区分开
            if (titles.Contains(title))
            {
                string parent = new DirectoryInfo(directory).Name;
                title = parent.Length > 0 ? $"{title}（{parent}）" : title;
                if (titles.Contains(title)) return false;
            }

            // 图片自己就是预览图；视频找同名的 jpg/png 当缩略图
            string preview = type == "image" ? name : FindSidecarPreview(directory, title);

            string id = "media_" + Hash(file + "|" + File.GetLastWriteTimeUtc(file).Ticks);
            var item = new WallpaperItem(id, title, type, directory, name, preview, "media");

            if (!found.TryAdd(file, item)) return false;

            titles.Add(title);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>视频旁边常放着同名封面（xxx.mp4 + xxx.jpg），有就拿来做缩略图。</summary>
    static string FindSidecarPreview(string directory, string baseName)
    {
        foreach (string ext in new[] { ".jpg", ".png", ".webp", ".jpeg", ".gif" })
        {
            string candidate = Path.Combine(directory, baseName + ext);
            if (File.Exists(candidate)) return baseName + ext;
        }

        return "";
    }

    /// <summary>
    /// 要扫的根目录和各自深度：自定义目录 + 图片 / 视频 / 桌面（后三者可由开关关掉）。
    /// 桌面只扫一层——它下面常放着一堆工程/数据集目录，深扫会把列表灌满没用的图。
    /// </summary>
    static IEnumerable<(string Root, int Depth)> MediaRoots(WallpaperConfig config, int depth)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in (config.MediaDirectories ?? "")
                     .Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string full = SafeFullPath(raw.Trim().Trim('"'));
            if (full.Length > 0 && Directory.Exists(full) && seen.Add(full)) yield return (full, depth);
        }

        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        // 推荐投放点：插件自己建，用户把图丢进去就能选，不用手填路径。
        // 这个目录始终生效（里面只会有用户自己放的东西），系统那几个大目录才受开关控制。
        string drop = "";
        if (!string.IsNullOrWhiteSpace(pictures))
        {
            drop = Path.Combine(pictures, "AlifeWallpaper");
            try { Directory.CreateDirectory(drop); }
            catch { drop = ""; }
        }

        if (!string.IsNullOrWhiteSpace(drop))
        {
            string dropFull = SafeFullPath(drop);
            if (dropFull.Length > 0 && Directory.Exists(dropFull) && seen.Add(dropFull))
                yield return (dropFull, depth);
        }

        if (!config.ScanUserFolders) yield break;

        foreach ((string? candidate, int want) in new[]
                 {
                     (videos, Math.Min(depth, 2)),
                     // 桌面只看根目录：它下面常是工程/数据集，钻进去只会把列表灌满
                     (desktop, 0),
                     (pictures, depth),
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            string full = SafeFullPath(candidate);
            if (full.Length > 0 && Directory.Exists(full) && seen.Add(full)) yield return (full, want);
        }
    }

    static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return ""; }
    }

    static IEnumerable<string> SplitDirectories(string raw)
        => (raw ?? "")
            .Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim().Trim('"'))
            .Where(Directory.Exists);

    static string[] SplitKeywords(string raw)
        => (raw ?? "")
            .Split(new[] { ',', '，', ';', '；', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>读取 Steam 安装目录与 libraryfolders.vdf 里的所有库。</summary>
    static IEnumerable<string> SteamLibraries()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? steam = null;
        try { steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; } catch { }
        if (string.IsNullOrWhiteSpace(steam))
            try { steam = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string; } catch { }
        if (string.IsNullOrWhiteSpace(steam))
            steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");

        Add(libraries, steam);

        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                Add(libraries, match.Groups[1].Value.Replace("\\\\", "\\"));
        }

        return libraries;
    }

    static void Add(HashSet<string> libraries, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string full = Path.GetFullPath(path);
            if (Directory.Exists(full)) libraries.Add(full);
        }
        catch { }
    }
}
