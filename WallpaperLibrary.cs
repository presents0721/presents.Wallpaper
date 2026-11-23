using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace presents.Wallpaper;

/// <summary>
/// 壁纸库的「隐藏」与「回收站」。
///
/// 隐藏：只登记（路径 + ID + 标题），文件一个字节都不动 —— 创意工坊缓存的壁纸被 Steam / WE 校验改写也不会受影响，
///       随时「恢复」就是把它从清单里划掉，列表里就又出来了。
/// 删除：把文件（自定义图片 / 视频）或整个项目目录（创意工坊缓存 / 本地项目）挪到插件目录下的 Recycle，
///       同时留下一条存档，还原时按原路径搬回去。
///
/// 为什么不用「重扫时按清单过滤」来实现删除：那样一还原就得重扫整库，
/// 会把之前删掉、但文件还躺在原地的壁纸一起捞回来。真正的文件搬移 + 单条还原没有这个副作用。
/// </summary>
internal sealed class WallpaperLibrary
{
    internal sealed class HiddenEntry
    {
        /// <summary>稳定标识：自定义素材是文件绝对路径，引擎壁纸是项目目录绝对路径。</summary>
        public string Key { get; set; } = "";
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Type { get; set; } = "";
        /// <summary>workshop / project / custom / media</summary>
        public string Source { get; set; } = "";
        public string At { get; set; } = "";
    }

    internal sealed class TrashEntry
    {
        public string Key { get; set; } = "";
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Type { get; set; } = "";
        public string Source { get; set; } = "";
        /// <summary>原始路径（自定义素材是文件，引擎壁纸是项目目录）。</summary>
        public string Path { get; set; } = "";
        /// <summary>挪进回收站后的路径；空 = 文件被占用没搬走，只做了登记（下次还能还原成隐藏）。</summary>
        public string Stored { get; set; } = "";
        public string At { get; set; } = "";
    }

    readonly object gate = new();
    readonly List<HiddenEntry> hidden = new();
    readonly List<TrashEntry> trash = new();

    string pluginDirectory = "";
    string hiddenFile = "";
    string trashFile = "";
    string recycleRoot = "";

    internal string RecycleRoot => recycleRoot;

    internal IReadOnlyList<HiddenEntry> Hidden { get { lock (gate) return hidden.ToList(); } }
    internal IReadOnlyList<TrashEntry> Trash { get { lock (gate) return trash.ToList(); } }
    internal int HiddenCount { get { lock (gate) return hidden.Count; } }
    internal int TrashCount { get { lock (gate) return trash.Count; } }

    /// <summary>壁纸的稳定身份：改过名 / 改过 mtime 也认得出同一个文件。</summary>
    internal static string KeyOf(WallpaperItem item)
    {
        try
        {
            if (item.IsUserMedia)
                return Path.GetFullPath(Path.Combine(item.Root, item.File));

            return Path.GetFullPath(item.Root);
        }
        catch
        {
            return item.Id;
        }
    }

    internal void Init(string directory)
    {
        pluginDirectory = directory;
        hiddenFile = Path.Combine(directory, "hidden.json");
        trashFile = Path.Combine(directory, "trash.json");
        recycleRoot = Path.Combine(directory, "Recycle");

        lock (gate)
        {
            hidden.Clear();
            hidden.AddRange(Load<HiddenEntry>(hiddenFile));

            trash.Clear();
            trash.AddRange(Load<TrashEntry>(trashFile));

            // 回收站里已经没有的东西（用户手动清了 Recycle 目录）就别再列出来
            trash.RemoveAll(x => string.IsNullOrEmpty(x.Path));
        }
    }

    internal bool IsHidden(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        lock (gate)
            return hidden.Any(x => Same(x.Key, key));
    }

    internal bool IsHidden(WallpaperItem item) => IsHidden(KeyOf(item));

    internal bool IsTrashed(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        lock (gate)
            return trash.Any(x => Same(x.Key, key));
    }

    internal static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>隐藏：只登记，不碰文件。</summary>
    internal void Hide(WallpaperItem item)
    {
        string key = KeyOf(item);

        lock (gate)
        {
            if (hidden.Any(x => Same(x.Key, key))) return;

            hidden.Add(new HiddenEntry
            {
                Key = key,
                Id = item.Id,
                Title = item.Title,
                Type = item.Type,
                Source = item.Source,
                At = Now()
            });
        }

        SaveHidden();
    }

    /// <summary>取消隐藏（隐藏项「恢复」）。返回值有没有真的删掉一条。</summary>
    internal bool Unhide(string key)
    {
        int removed;

        lock (gate)
            removed = hidden.RemoveAll(x => Same(x.Key, key) || Same(x.Id, key));

        if (removed > 0) SaveHidden();
        return removed > 0;
    }

    /// <summary>
    /// 删除：文件 / 项目目录搬进回收站。
    /// 返回 (是否成功, 失败原因)；失败时不登记回收站记录，避免出现「点了删除但列表里还在」的鬼状态。
    /// </summary>
    internal (bool Ok, string Message, TrashEntry? Entry) Delete(WallpaperItem item)
    {
        string key = KeyOf(item);
        string target = key;
        bool isDirectory = false;

        try
        {
            if (item.IsUserMedia)
            {
                target = Path.GetFullPath(Path.Combine(item.Root, item.File));
                isDirectory = Directory.Exists(target) && !File.Exists(target);
            }
            else
            {
                isDirectory = Directory.Exists(target);
            }

            if (!File.Exists(target) && !Directory.Exists(target))
                return (false, "文件或目录已经不存在了，刷新列表即可", null);

            Directory.CreateDirectory(recycleRoot);

            string stored = Path.Combine(recycleRoot, NextName(Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar)), isDirectory));

            if (isDirectory) Directory.Move(target, stored);
            else File.Move(target, stored);

            var entry = new TrashEntry
            {
                Key = key,
                Id = item.Id,
                Title = item.Title,
                Type = item.Type,
                Source = item.Source,
                Path = target,
                Stored = stored,
                At = Now()
            };

            lock (gate)
            {
                hidden.RemoveAll(x => Same(x.Key, key));
                trash.RemoveAll(x => Same(x.Key, key));
                trash.Add(entry);
            }

            SaveHidden();
            SaveTrash();

            return (true, isDirectory ? "已把整个项目目录移入回收站" : "已移入回收站", entry);
        }
        catch (Exception ex)
        {
            return (false, "移入回收站失败：" + ex.Message, null);
        }
    }

    /// <summary>
    /// 从回收站还原**单条**。只搬这一条，不重扫整库，也不会顺带把别的已删项捞回来。
    /// </summary>
    internal (bool Ok, string Message) Restore(string key)
    {
        TrashEntry? entry;

        lock (gate)
            entry = trash.FirstOrDefault(x => Same(x.Key, key) || Same(x.Id, key));

        if (entry == null) return (false, "回收站里没有这一条了");

        try
        {
            bool storedExists = !string.IsNullOrEmpty(entry.Stored) &&
                                (File.Exists(entry.Stored) || Directory.Exists(entry.Stored));

            if (storedExists)
            {
                string? parent = Path.GetDirectoryName(entry.Path);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                // 原位置已经有同名文件（用户又丢了一份进去）：让开，别覆盖人家的新文件
                string destination = entry.Path;
                if (File.Exists(destination) || Directory.Exists(destination))
                    destination = UniqueSibling(destination);

                if (Directory.Exists(entry.Stored)) Directory.Move(entry.Stored, destination);
                else File.Move(entry.Stored, destination, true);

                entry.Path = destination;
            }

            lock (gate) trash.RemoveAll(x => ReferenceEquals(x, entry) || Same(x.Key, entry.Key));
            SaveTrash();

            return (true, storedExists ? "已还原到原位置" : "已从清单移除（文件当时没能搬走）");
        }
        catch (Exception ex)
        {
            return (false, "还原失败：" + ex.Message);
        }
    }

    /// <summary>彻底删除：真删回收站里的文件 / 目录，然后从清单移除。</summary>
    internal (bool Ok, string Message) Purge(string key)
    {
        TrashEntry? entry;

        lock (gate)
            entry = trash.FirstOrDefault(x => Same(x.Key, key) || Same(x.Id, key));

        if (entry == null) return (false, "回收站里没有这一条了");

        try
        {
            if (!string.IsNullOrEmpty(entry.Stored))
            {
                if (Directory.Exists(entry.Stored)) Directory.Delete(entry.Stored, true);
                else if (File.Exists(entry.Stored)) File.Delete(entry.Stored);
            }

            lock (gate) trash.RemoveAll(x => ReferenceEquals(x, entry) || Same(x.Key, entry.Key));
            SaveTrash();

            return (true, "已彻底删除");
        }
        catch (Exception ex)
        {
            return (false, "彻底删除失败：" + ex.Message);
        }
    }

    // ---------------- 内部 ----------------

    static bool Same(string? a, string? b)
        => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>回收站里的落盘名：时间戳前缀，避免同名文件互相覆盖。</summary>
    static string NextName(string name, bool isDirectory)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string safe = Sanitize(string.IsNullOrWhiteSpace(name) ? (isDirectory ? "project" : "file") : name);

        return stamp + "_" + safe;
    }

    static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        return name.Length > 80 ? name[..80] : name;
    }

    /// <summary>原位置被占用时换个名字放回去：xxx (2).jpg</summary>
    static string UniqueSibling(string path)
    {
        bool isDirectory = Directory.Exists(path);
        string directory = Path.GetDirectoryName(path) ?? "";
        string baseName = isDirectory ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) : Path.GetFileNameWithoutExtension(path);
        string extension = isDirectory ? "" : Path.GetExtension(path);

        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(directory, $"{baseName} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        return path;
    }

    static List<T> Load<T>(string file)
    {
        try
        {
            if (!File.Exists(file)) return new List<T>();

            string json = File.ReadAllText(file);
            return JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>();
        }
        catch
        {
            return new List<T>();
        }
    }

    void SaveHidden()
    {
        List<HiddenEntry> snapshot;
        lock (gate) snapshot = hidden.ToList();

        try
        {
            File.WriteAllText(hiddenFile, JsonSerializer.Serialize(snapshot, Pretty));
        }
        catch { }
    }

    void SaveTrash()
    {
        List<TrashEntry> snapshot;
        lock (gate) snapshot = trash.ToList();

        try
        {
            File.WriteAllText(trashFile, JsonSerializer.Serialize(snapshot, Pretty));
        }
        catch { }
    }

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
}
