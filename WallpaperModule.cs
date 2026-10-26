using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace presents.Wallpaper;

/// <summary>
/// Alife 界面背景壁纸：扫描本机 Wallpaper Engine 壁纸，注入到 Alife 主窗口的 Blazor 页面里，
/// 作为聊天界面的背景层，并把这些能力暴露成 AI 可调用的 XML 函数。
/// </summary>
[Module("wallpaper壁纸",
    "把 Wallpaper Engine 的视频 / 网页壁纸作为 Alife 界面背景：自动扫描 Steam 创意工坊与本地项目，支持轮播，并可让 AI 按情绪 / 标签切换壁纸。",
    defaultCategory: "presents/窗口美化",
    globalUI: typeof(WallpaperGlobalUI))]
public sealed class WallpaperModule(
    PluginSystem pluginSystem,
    XmlFunctionCaller functionCaller,
    StorageSystem storage,
    ConfigurationSystem configurationSystem,
    ILogger<WallpaperModule> logger,
    Interactor<WallpaperModule> interactor) : ChatBehaviour, IConfigurable<WallpaperConfig>
{
    public WallpaperConfig Configuration { get; set; } = null!;

    readonly WallpaperCatalog catalog = new();

    /// <summary>隐藏清单 + 回收站（删除的文件真的搬走，单条还原，不重扫整库）。</summary>
    readonly WallpaperLibrary library = new();

    LocalStaticServer? server;
    AlifeBackground? background;
    XmlHandler? handler;
    WallpaperItem? current;
    int index = -1;
    long lastRotation;
    long lastHealthCheck;
    bool rotating;
    bool checking;
    string pluginDirectory = "";

    const string LastWallpaperKey = "presents.Wallpaper/Last";

    /// <summary>
    /// 当前活跃的模块实例。Alife 5.0 起页面脚本执行器只在主界面渲染后才可用，
    /// 由 <see cref="WallpaperGlobalUI"/> 挂载时回调 <see cref="OnUiReadyAsync"/>，靠这里找到实例补注入壁纸。
    /// </summary>
    internal static WallpaperModule? Active { get; private set; }

    protected override Task OnAwake()
    {
        try
        {
            pluginDirectory = pluginSystem.PluginContext.GetPluginDirectoryPath("presents.Wallpaper");
            WallpaperProps.Store.Init(pluginDirectory);
            library.Init(pluginDirectory);
            catalog.Refresh(Configuration);
            server = new LocalStaticServer(Path.Combine(pluginDirectory, "Resources", "wwwroot"));
            server.ApiHandler = HandleApiAsync;
            server.PreviewResolver = ResolvePreview;
            server.WebPropsProvider = BuildWebPropsJson;

            // 端口写进临时目录，方便本机排查（/api/state、/api/dom）
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "alife-wallpaper.port"), server.Port.ToString()); } catch { }
            background = new AlifeBackground(logger);

            logger.LogInformation("[WallpaperEngine] 壁纸库：{Summary}；静态服务 {Url}", catalog.LastSummary, server.BaseUrl);

            handler = new XmlHandler(this)
            {
                Description = "控制 Alife 界面背景壁纸：按关键词或标签切换、下一张/上一张、列出壁纸库与标签、查询当前壁纸、停止显示、重新扫描。"
            };
            functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[WallpaperEngine] 初始化失败");
        }

        return Task.CompletedTask;
    }

    protected override async Task OnStart()
    {
        Active = this;

        if (!Configuration.AutoStart) return;

        try
        {
            if (catalog.Count == 0) catalog.Refresh(Configuration);

            WallpaperItem? item = null;

            if (!string.IsNullOrWhiteSpace(Configuration.DefaultWallpaper))
                item = FindVisible(Configuration.DefaultWallpaper);

            if (item == null && Configuration.RestoreLast)
            {
                string? lastId = null;
                try { lastId = storage.GetObject<string>(LastWallpaperKey); } catch { }
                // 上次那张如果已经被隐藏 / 删除，就不该再恢复出来
                if (!string.IsNullOrWhiteSpace(lastId)) item = FindVisible(lastId);
            }

            // 只有「没有任何记录」时才优先视频/网页，避免首次启动只显示场景预览图。
            // 注意：DefaultWallpaper / RestoreLast 命中的那张，哪怕 playable=false（场景壁纸）也要原样恢复——
            // 场景壁纸开着实时渲染（LiveScene）时本来就是动的，不能因为它有封面图就把用户选的换掉。
            // 之前这里无条件换成第一张可播放壁纸，导致每次重载都跳回同一张视频，看起来像"选择没被保存"。
            List<WallpaperItem> visible = VisibleItems();
            if (item == null) item = visible.FirstOrDefault(x => x.IsPlayable);
            if (item == null && visible.Count > 0) item = visible[0];

            if (item != null)
            {
                await PlayAsync(item);
            }
            else
            {
                logger.LogWarning("[WallpaperEngine] 没有找到可用壁纸；请在 Wallpaper Engine 订阅壁纸，或在模块设置里添加额外目录");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[WallpaperEngine] 自动启动壁纸失败");
        }
    }

    protected override Task OnUpdate()
    {
        long now = Environment.TickCount64;

        // 页面重载 / F5 后注入的层会消失，这里做一次轻量自愈
        if (current != null && !checking && now - lastHealthCheck >= 1500)
        {
            lastHealthCheck = now;
            checking = true;
            _ = CheckAliveAsync();
        }

        if (Configuration.RotationMinutes <= 0 || catalog.Count < 2 || rotating || current == null)
            return Task.CompletedTask;

        if (lastRotation == 0)
        {
            lastRotation = now;
            return Task.CompletedTask;
        }

        if (now - lastRotation < Configuration.RotationMinutes * 60000L)
            return Task.CompletedTask;

        rotating = true;
        _ = RotateAsync();
        return Task.CompletedTask;
    }

    async Task CheckAliveAsync()
    {
        try
        {
            if (background == null || current == null) return;
            if (await background.IsLayerAliveAsync()) return;

            logger.LogInformation("[WallpaperEngine] 界面背景层丢失，重新注入");
            await background.ShowAsync(current, Configuration, server!.BaseUrl);
        }
        catch (Exception ex)
        {
            logger.LogWarning("[WallpaperEngine] 背景自愈失败：" + ex.Message);
        }
        finally
        {
            checking = false;
        }
    }

    async Task RotateAsync()
    {
        try { await StepAsync(1); }
        catch (Exception ex) { logger.LogWarning("[WallpaperEngine] 自动轮播失败：" + ex.Message); }
        finally { rotating = false; }
    }

    protected override async Task OnDestroy()
    {
        try
        {
            if (handler != null) functionCaller.UnregisterHandler(handler);
        }
        catch { }

        try
        {
            if (background != null) await background.ClearAsync();
        }
        catch { }

        try { server?.Dispose(); } catch { }
        server = null;

        try { File.Delete(Path.Combine(Path.GetTempPath(), "alife-wallpaper.port")); } catch { }

        if (ReferenceEquals(Active, this)) Active = null;
    }

    /// <summary>
    /// 主界面（Blazor）挂载完成时由 <see cref="WallpaperGlobalUI"/> 回调。
    /// 5.0 起页面脚本执行器（<see cref="AlifeBackground.JsRunner"/>）只有在 UI 渲染后才可用，
    /// 所以这里把当前选中的壁纸补注入一次：首启时页面未就绪、或页面刷新后层丢失，都靠它兜底。
    /// </summary>
    internal static async Task OnUiReadyAsync()
    {
        WallpaperModule? module = Active;
        if (module == null) return;

        await module.ReattachAsync();
    }

    /// <summary>界面就绪后把当前壁纸补注入一次；首启页面未就绪、或页面刷新后层丢失，都靠它兜底。</summary>
    async Task ReattachAsync()
    {
        if (current == null || server == null) return;

        background ??= new AlifeBackground(logger);

        try
        {
            await background.ShowAsync(current, Configuration, server.BaseUrl);
        }
        catch (Exception ex)
        {
            logger.LogWarning("[WallpaperEngine] 界面就绪后补注入失败：" + ex.Message);
        }
    }

    // ---------------- 运行逻辑 ----------------

    async Task EnsureBackgroundAsync()
    {
        if (server == null) throw new InvalidOperationException("壁纸静态服务未就绪，请重载插件");

        background ??= new AlifeBackground(logger);
        await background.AttachAsync();
    }

    async Task PlayAsync(WallpaperItem item)
    {
        await EnsureBackgroundAsync();

        server!.SetWallpaperRoot(item.Root);
        current = item;
        index = catalog.IndexOf(item);
        lastRotation = Environment.TickCount64;

        await background!.ShowAsync(item, Configuration, server.BaseUrl);
        try { storage.SetObject(LastWallpaperKey, item.Id); } catch { }

        logger.LogInformation("[WallpaperEngine] 界面背景 → {Title}（{Type} / {Source}）", item.Title, item.Type, item.Source);
    }

    async Task StopAsync(bool keepWidget = false)
    {
        if (background != null) await background.ClearAsync(keepWidget);
        current = null;
        index = -1;
        server?.SetWallpaperRoot(null);
    }

    async Task StepAsync(int delta)
    {
        if (catalog.Count == 0) catalog.Refresh(Configuration);

        // 只在可见列表里走：隐藏的那几张不该被上下张翻出来
        WallpaperItem? item = StepVisible(delta);
        if (item == null)
        {
            interactor.Poke("壁纸库为空：先在 Wallpaper Engine 订阅壁纸，或在「自定义壁纸目录」里填文件夹；也可以直接把图片 / 视频丢进 图片\\AlifeWallpaper。");
            return;
        }

        await PlayAsync(item);
        interactor.Poke($"已切换到壁纸：{item.Title}（{item.Type}）。");
    }

    // ---------------- 挂件 HTTP API ----------------

    /// <summary>把内存里的配置写回存储，否则重载插件 / 重启后挂件调的参数会丢。</summary>
    void PersistConfig()
    {
        try
        {
            configurationSystem.SetConfiguration(GetType(), Configuration, Character.StorageKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning("[WallpaperEngine] 保存配置失败：" + ex.Message);
        }
    }

    /// <summary>网页壁纸入口 HTML 里要带的属性种子（只带用户改过的值）。</summary>
    string BuildWebPropsJson()
    {
        if (current == null) return "{}";
        if (!string.Equals(current.Type, "web", StringComparison.OrdinalIgnoreCase)) return "{}";

        try
        {
            var props = WallpaperProps.Resolve(current, WallpaperProps.Store.Get(current.Id));
            // 网页壁纸拿到的也是 WE 的 { value, type } 形状（页面里读 p.xxx.value）
        return JsonSerializer.Serialize(WallpaperProps.NativeNodes(props));
        }
        catch
        {
            return "{}";
        }
    }

    string? ResolvePreview(string id)
    {
        WallpaperItem? item = catalog.Find(id);
        if (item == null || string.IsNullOrEmpty(item.Preview)) return null;

        try
        {
            string full = Path.Combine(item.Root, item.Preview.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    async Task<string> HandleApiAsync(string path, string query)
    {
        Dictionary<string, string> args = ParseQuery(query);

        switch (path.TrimEnd('/').ToLowerInvariant())
        {
            case "/api/state":
            {
                // 用户刚把图片丢进素材目录时，挂件下一次轮询就能看到（不用重启插件）
                if ((DateTime.UtcNow - catalog.LastScanUtc).TotalSeconds > 120)
                    catalog.Refresh(Configuration);

                // 实时渲染到底起没起来：起不来时页面只剩预览图，调什么都像"没反应"
                string scene = background == null ? "" : await background.EvalAsync("window.__alifeWpScene ? 'up' : 'down'");
                return BuildStateJson("", scene.Trim() == "up" ? "up" : "down");
            }

            // 手动重扫素材（放完新图不想等 2 分钟就点这个）
            case "/api/rescan":
            {
                catalog.Refresh(Configuration);
                return BuildStateJson();
            }

            // 挂件「我的」里点「＋ 文件夹」：弹系统目录选择，选完立刻扫描并记住
            case "/api/pickdir":
            {
                string? picked = await PickMediaDirectoryAsync();

                if (!string.IsNullOrWhiteSpace(picked))
                {
                    AddMediaDirectory(picked);
                    PersistConfig();
                    catalog.Refresh(Configuration);
                }

                return BuildStateJson();
            }

            case "/api/forgetdir":
            {
                RemoveMediaDirectory(args.GetValueOrDefault("path"));
                PersistConfig();
                catalog.Refresh(Configuration);
                return BuildStateJson();
            }

            // 在资源管理器里打开目录：没填路径就打开推荐投放目录
            case "/api/reveal":
            {
                RevealMediaDirectory(args.GetValueOrDefault("path"));
                return BuildStateJson();
            }

            case "/api/switch":
            {
                WallpaperItem? item = FindVisible(args.GetValueOrDefault("id"));

                // 找不到就重扫一次：常见于刚丢进素材目录、还没到自动重扫时间
                if (item == null)
                {
                    catalog.Refresh(Configuration);
                    item = FindVisible(args.GetValueOrDefault("id"));
                }

                if (item == null) return "{\"ok\":false,\"error\":\"没有找到这张壁纸\"}";

                await PlayAsync(item);
                return BuildStateJson();
            }

            // 隐藏：只登记，不动文件。创意工坊缓存那种不方便展示的壁纸用这个
            case "/api/hide":
            {
                WallpaperItem? item = catalog.Find(args.GetValueOrDefault("id"));
                if (item == null) return "{\"ok\":false,\"error\":\"没有找到这张壁纸\"}";

                library.Hide(item);
                logger.LogInformation("[WallpaperEngine] 已隐藏：{Title}", item.Title);
                return BuildStateJson();
            }

            // 取消隐藏（隐藏列表里点「恢复」）
            case "/api/unhide":
            {
                string key = args.GetValueOrDefault("key") ?? args.GetValueOrDefault("id") ?? "";
                library.Unhide(key);
                return BuildStateJson();
            }

            // 删除：搬进回收站。只更新列表，不做重新加载（当前那张保持原样，不会闪一下）
            case "/api/delete":
            {
                WallpaperItem? item = catalog.Find(args.GetValueOrDefault("id"));
                if (item == null) return "{\"ok\":false,\"error\":\"没有找到这张壁纸\"}";

                (bool ok, string message, WallpaperLibrary.TrashEntry? entry) = library.Delete(item);
                _ = entry;
                if (!ok) return JsonSerializer.Serialize(new { ok = false, error = message });

                // 只把这一条从列表里摘掉，不整库重扫、也不重新加载壁纸
                catalog.Remove(item);
                logger.LogInformation("[WallpaperEngine] {Title} → {Message}", item.Title, message);
                return BuildStateJson();
            }

            // 从回收站还原单条：只搬这一条，不重扫整库，不会把别的已删项一起捞回来
            case "/api/restore":
            {
                string key = args.GetValueOrDefault("key") ?? args.GetValueOrDefault("id") ?? "";
                (bool ok, string message) = library.Restore(key);
                if (!ok) return JsonSerializer.Serialize(new { ok = false, error = message });

                catalog.Refresh(Configuration);
                logger.LogInformation("[WallpaperEngine] 回收站还原：{Message}", message);
                return BuildStateJson();
            }

            // 彻底删除：真删回收站里的文件 / 目录
            case "/api/purge":
            {
                string key = args.GetValueOrDefault("key") ?? args.GetValueOrDefault("id") ?? "";
                (bool ok, string message) = library.Purge(key);
                if (!ok) return JsonSerializer.Serialize(new { ok = false, error = message });

                logger.LogInformation("[WallpaperEngine] 回收站彻底删除：{Message}", message);
                return BuildStateJson();
            }

            case "/api/step":
            {
                int delta = args.TryGetValue("delta", out string? raw) && int.TryParse(raw, out int step) ? step : 1;
                await StepAsync(delta);
                return BuildStateJson();
            }

            case "/api/dom":
            {
                if (background == null) return "{\"ok\":false,\"error\":\"背景层未初始化\"}";

                string dump = await background.DumpDomAsync();
                return string.IsNullOrWhiteSpace(dump) ? "{\"ok\":false,\"error\":\"页面未就绪\"}" : dump;
            }

            // 实时渲染退回预览图后重新挂载（面板上的「重新挂载」按钮）
            case "/api/remount":
            {
                if (background == null) return "{\"ok\":false,\"error\":\"背景层未初始化\"}";
                return await background.RemountSceneAsync();
            }

            // 本机排查：场景运行时状态（脚本报错 / 跳过的脚本 / 当前属性值）
            case "/api/scene":
            {
                if (background == null) return "{\"ok\":false,\"error\":\"背景层未初始化\"}";
                return await background.SceneDiagAsync();
            }

            // 本机排查：在页面里执行任意 JS
            case "/api/eval":
            {
                if (background == null) return "{\"ok\":false,\"error\":\"背景层未初始化\"}";

                string js = args.GetValueOrDefault("js") ?? "";
                if (js.Length == 0) return "{\"ok\":false,\"error\":\"缺少 js\"}";

                return await background.EvalAsync(js);
            }

            case "/api/config":
            {
                if (args.TryGetValue("opacity", out string? op) && int.TryParse(op, out int o))
                    Configuration.Opacity = Math.Clamp(o, 0, 100);
                if (args.TryGetValue("blur", out string? bl) && int.TryParse(bl, out int b))
                    Configuration.Blur = Math.Clamp(b, 0, 30);
                if (args.TryGetValue("panel", out string? pa) && int.TryParse(pa, out int p))
                    Configuration.PanelOpacity = Math.Clamp(p, 0, 100);
                if (args.TryGetValue("white", out string? wh) && wh.Length > 0)
                    Configuration.WhiteScrim = wh == "1" || wh.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (args.TryGetValue("br", out string? br) && int.TryParse(br, out int bv))
                    Configuration.Brightness = Math.Clamp(bv, 0, 200);
                if (args.TryGetValue("ct", out string? ct) && int.TryParse(ct, out int cv))
                    Configuration.Contrast = Math.Clamp(cv, 0, 200);
                if (args.TryGetValue("sa", out string? sa) && int.TryParse(sa, out int sv))
                    Configuration.Saturate = Math.Clamp(sv, 0, 200);
                if (args.TryGetValue("bu", out string? bu) && int.TryParse(bu, out int buv))
                    Configuration.BubbleOpacity = Math.Clamp(buv, 0, 100);
                if (args.TryGetValue("px", out string? px) && int.TryParse(px, out int pxv))
                    Configuration.Parallax = Math.Clamp(pxv, 0, 100);
                // 挂件设置面板自己的不透明度（跟页面卡片的「白度」 / panel 互不影响）
                if (args.TryGetValue("wpo", out string? wp) && int.TryParse(wp, out int wpv))
                    Configuration.WidgetPanelOpacity = Math.Clamp(wpv, 0, 100);

                // 声音类参数只改 <video>，不碰样式层
                bool media = false;
                if (args.TryGetValue("vol", out string? vo) && int.TryParse(vo, out int vv))
                {
                    Configuration.Volume = Math.Clamp(vv, 0, 200);
                    media = true;
                }
                if (args.TryGetValue("rate", out string? rt) && int.TryParse(rt, out int rv))
                {
                    Configuration.PlaybackRate = Math.Clamp(rv, 50, 200);
                    media = true;
                }
                if (args.TryGetValue("mute", out string? mu) && mu.Length > 0)
                {
                    Configuration.Mute = mu == "1" || mu.Equals("true", StringComparison.OrdinalIgnoreCase);
                    media = true;
                }

                bool restyle = args.ContainsKey("opacity") || args.ContainsKey("blur") || args.ContainsKey("panel")
                    || args.ContainsKey("white") || args.ContainsKey("br") || args.ContainsKey("ct")
                    || args.ContainsKey("sa") || args.ContainsKey("bu") || args.ContainsKey("px");

                PersistConfig();
                if (background != null)
                {
                    if (restyle) await background.RestyleAsync(Configuration);
                    if (media) await background.ApplyMediaAsync(Configuration);
                }
                return BuildStateJson();
            }

            // 挂件拖动后的位置。x / y = -1 表示归位到默认右下角
            case "/api/move":
            {
                int x = args.TryGetValue("x", out string? xs) && int.TryParse(xs, out int xv) ? xv : -1;
                int y = args.TryGetValue("y", out string? ys) && int.TryParse(ys, out int yv) ? yv : -1;

                Configuration.WidgetX = x < 0 ? -1 : x;
                Configuration.WidgetY = y < 0 ? -1 : y;
                PersistConfig();
                // 只回答 ok：拖动结束时每拖一次都回传整份列表没必要
                return "{\"ok\":true}";
            }

            case "/api/prop":
            {
                if (current == null) return "{\"ok\":false,\"error\":\"当前没有壁纸\"}";

                string name = args.GetValueOrDefault("name") ?? "";
                string raw = args.GetValueOrDefault("value") ?? "";
                bool reset = IsTrue(args.GetValueOrDefault("reset"));

                // 作者默认值（不吃用户覆盖）：还原时必须把它推回正在跑的场景，
                // 光删存档的话场景里还留着上一次调的值
                List<WallpaperProps.Prop> defaults = WallpaperProps.Resolve(current, null);
                Dictionary<string, object?> push;

                if (reset && name.Length == 0)
                {
                    // 全部还原：先看改过哪些，再清存档，然后逐个推回默认值
                    Dictionary<string, string>? old = WallpaperProps.Store.Get(current.Id);
                    WallpaperProps.Store.Clear(current.Id);

                    push = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    if (old != null)
                    {
                        foreach (string key in old.Keys)
                        {
                            WallpaperProps.Prop? def = WallpaperProps.Find(defaults, key);
                            if (def != null && def.Value != null) push[def.Name] = WallpaperProps.Node(def);
                        }
                    }
                }
                else
                {
                    if (name.Length == 0) return "{\"ok\":false,\"error\":\"缺少属性名\"}";

                    if (reset)
                    {
                        // 单个还原：只把这一条的默认值推回去，别人的值不动
                        WallpaperProps.Store.Remove(current.Id, name);

                        push = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        WallpaperProps.Prop? def = WallpaperProps.Find(defaults, name);
                        if (def != null && def.Value != null) push[def.Name] = WallpaperProps.Node(def);
                    }
                    else
                    {
                        try { using JsonDocument probe = JsonDocument.Parse(raw); }
                        catch { return "{\"ok\":false,\"error\":\"属性值不是合法 JSON\"}"; }

                        WallpaperProps.Store.Set(current.Id, name, raw);

                        // 只推这一条：拖滑条时每帧都推全量会把贴图属性反复重载
                        push = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        WallpaperProps.Prop? one = WallpaperProps.Find(defaults, name);
                        if (one != null && WallpaperProps.TryNode(raw, one.Kind, out Dictionary<string, object?>? node))
                            push[one.Name] = node;

                        if (push.Count == 0)
                        {
                            // 名字对不上（作者改过属性名 / 存档里是脏数据）时退回全量
                            push = WallpaperProps.NativeNodes(WallpaperProps.Resolve(current, WallpaperProps.Store.Get(current.Id)));
                        }
                    }
                }

                string applied = background == null ? "no-window" : await background.ApplyPropsAsync(push);
                return BuildStateJson(applied);
            }

            case "/api/clear":
            {
                // 用户点“清除壁纸”：只撤壁纸层，保留右下角挂件，否则挂件没了就再也叫不回来
                await StopAsync(keepWidget: true);
                return BuildStateJson();
            }
        }

        return "{\"ok\":false,\"error\":\"unknown api\"}";
    }

    // ---------------- 隐藏 / 回收站 ----------------

    /// <summary>
    /// 列表里真正要显示的壁纸：扫描结果里去掉被隐藏的、去掉在回收站里的。
    /// 回收站也要过滤，是因为「删除失败但已登记」的那几条文件还躺在原位，重扫会把它们捞回来。
    /// </summary>
    List<WallpaperItem> VisibleItems()
        => catalog.Items.Where(x => !library.IsHidden(x) && !library.IsTrashed(WallpaperLibrary.KeyOf(x))).ToList();

    WallpaperItem? FindVisible(string? query)
    {
        List<WallpaperItem> visible = VisibleItems();
        if (visible.Count == 0) return null;

        if (string.IsNullOrWhiteSpace(query))
            return visible.FirstOrDefault(x => x.IsPlayable) ?? visible[0];

        string q = query.Trim();

        return visible.FirstOrDefault(x => x.Id.Equals(q, StringComparison.OrdinalIgnoreCase))
            ?? visible.FirstOrDefault(x => x.Title.Equals(q, StringComparison.CurrentCultureIgnoreCase))
            ?? visible.FirstOrDefault(x => x.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>上一张 / 下一张：只在可见列表里走，隐藏的会被跳过。</summary>
    WallpaperItem? StepVisible(int delta)
    {
        List<WallpaperItem> visible = VisibleItems();
        if (visible.Count == 0) return null;

        int at = current == null ? -1 : visible.FindIndex(x => x.Id == current.Id);
        if (at < 0) return delta >= 0 ? visible[0] : visible[^1];

        int i = (at + delta) % visible.Count;
        if (i < 0) i += visible.Count;
        return visible[i];
    }

    /// <param name="applied">上一次属性改动在页面侧的落点（scene / web / no-scene / scripts-off），给挂件提示用。</param>
    /// <param name="scene">实时渲染是否在跑：up / down（down = 页面只有预览图，调属性看不到变化）。</param>
    string BuildStateJson(string applied = "", string scene = "")
    {
        string? currentId = current?.Id;
        string baseUrl = server?.BaseUrl ?? "";

        // 隐藏的壁纸不进列表；回收站里的那些文件已经搬走了，扫描也扫不到
        List<WallpaperItem> visible = VisibleItems();

        var items = visible.Select(x => new
        {
            id = x.Id,
            title = x.Title,
            type = x.Type,
            playable = x.IsPlayable,
            // 挂件用 src 把「引擎壁纸」和「我的素材」分成两个标签，别混在一张列表里
            src = x.Source,
            current = x.Id == currentId,
            preview = string.IsNullOrEmpty(x.Preview) ? "" : baseUrl + "pv/" + Uri.EscapeDataString(x.Id)
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            ok = true,
            opacity = Math.Clamp(Configuration.Opacity, 0, 100),
            blur = Math.Clamp(Configuration.Blur, 0, 30),
            panel = Math.Clamp(Configuration.PanelOpacity, 0, 100),
            bubble = Math.Clamp(Configuration.BubbleOpacity, 0, 100),
            whiteScrim = Configuration.WhiteScrim,
            brightness = Math.Clamp(Configuration.Brightness, 0, 200),
            contrast = Math.Clamp(Configuration.Contrast, 0, 200),
            saturate = Math.Clamp(Configuration.Saturate, 0, 200),
            mute = Configuration.Mute,
            volume = Math.Clamp(Configuration.Volume, 0, 200),
            rate = Math.Clamp(Configuration.PlaybackRate, 50, 200),
            kind = current?.Type ?? "",
            liveScene = Configuration.LiveScene,
            // 场景脚本关着的话属性值只会被引擎存下来，画面上不会有变化：面板要能说清这件事
            scripts = Configuration.SceneScripts,
            scene,
            applied,
            parallax = Math.Clamp(Configuration.Parallax, 0, 100),
            // 右下角挂件的外观：0 = 经典胶囊，1 / 2 = Q 版大肥鱼形象
            petAvatar = Math.Clamp(Configuration.PetAvatar, 0, 2),
            petSize = Math.Clamp(Configuration.PetSize, 56, 120),
            widgetX = Configuration.WidgetX,
            widgetY = Configuration.WidgetY,
            // 挂件面板自己的不透明度（与页面卡片的「白度」分开）
            wpanel = Math.Clamp(Configuration.WidgetPanelOpacity, 0, 100) / 100.0,
            // 场景走 setProperties，网页走 postMessage 桥；视频没有属性通道
            props = (current != null && (
                        (string.Equals(current.Type, "scene", StringComparison.OrdinalIgnoreCase) && Configuration.LiveScene) ||
                        string.Equals(current.Type, "web", StringComparison.OrdinalIgnoreCase)))
                ? WallpaperProps.ForPanel(WallpaperProps.Resolve(current, WallpaperProps.Store.Get(current.Id)))
                : new List<object>(),
            // 数量对比：扫描到多少 / 列表里实际有多少（隐藏、排除词、跳过场景都会让两者对不上）
            count = visible.Count,
            // 两个标签各自的扫描基数：引擎壁纸 53 张里的 video 不该算进「我的」计数
            scanned = catalog.ScannedCount,
            scannedMedia = catalog.ScannedMediaCount,
            scannedEngine = catalog.ScannedEngineCount,
            // 「我的」标签用：数量、已添加的目录、推荐投放目录
            mediaCount = visible.Count(x => x.IsUserMedia),
            engineCount = visible.Count(x => !x.IsUserMedia),
            // 图片 / 视频只统计自己的素材，引擎壁纸的 video 类型不算
            videoCount = visible.Count(x => x.IsUserMedia && string.Equals(x.Type, "video", StringComparison.OrdinalIgnoreCase)),
            imageCount = visible.Count(x => x.IsUserMedia && string.Equals(x.Type, "image", StringComparison.OrdinalIgnoreCase)),
            sceneCount = visible.Count(x => string.Equals(x.Type, "scene", StringComparison.OrdinalIgnoreCase)),
            hiddenCount = library.HiddenCount,
            trashCount = library.TrashCount,
            hidden = library.Hidden.Select(x => new
            {
                key = x.Key,
                id = x.Id,
                title = x.Title,
                type = x.Type,
                src = x.Source,
                at = x.At
            }).ToList(),
            trash = library.Trash.Select(x => new
            {
                key = x.Key,
                id = x.Id,
                title = x.Title,
                type = x.Type,
                src = x.Source,
                at = x.At,
                // false = 当时文件被占用没能搬走，只做了登记
                moved = !string.IsNullOrEmpty(x.Stored)
            }).ToList(),
            mediaDirs = MediaDirectoryList(),
            dropDir = DefaultDropDirectory(),
            items
        });
    }

    // ---------------- 自定义素材目录 ----------------

    /// <summary>
    /// 弹系统目录选择框。Alife 5.0 是 WinForms 客户端，直接用 FolderBrowserDialog。
    /// 插件代码跑在后台线程上，而对话框要求 STA 线程并自建消息循环，
    /// 所以这里起一个临时 STA 线程来弹（Form.ShowDialog 自己会泵消息）。
    /// </summary>
    async Task<string?> PickMediaDirectoryAsync()
    {
        try
        {
            return await ShowFolderDialogAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[WallpaperEngine] 打开目录选择器失败");
            // 选不出来总得给点反馈，否则用户只看到按钮没反应
            interactor.Poke("打开文件夹选择器失败：" + ex.Message + "（也可以在模块设置里手填「自定义壁纸目录」）");
            return null;
        }
    }

    /// <summary>在独立的 STA 线程上弹 FolderBrowserDialog，返回选中的路径（取消则 null）。</summary>
    static Task<string?> ShowFolderDialogAsync()
    {
        TaskCompletionSource<string?> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        Thread thread = new(() =>
        {
            try
            {
                using System.Windows.Forms.FolderBrowserDialog dialog = new()
                {
                    Description = "选择壁纸目录（图片 / 视频 / Wallpaper Engine 项目）",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = true
                };

                completion.SetResult(
                    dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task;
    }

    List<string> MediaDirectoryList()
        => (Configuration.MediaDirectories ?? "")
            .Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim().Trim('"'))
            .Where(x => x.Length > 0)
            .ToList();

    void AddMediaDirectory(string path)
    {
        List<string> dirs = MediaDirectoryList();

        if (dirs.Any(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase))) return;

        dirs.Add(path);
        Configuration.MediaDirectories = string.Join("\n", dirs);
    }

    void RemoveMediaDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        List<string> dirs = MediaDirectoryList()
            .Where(x => !string.Equals(x, path, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Configuration.MediaDirectories = string.Join("\n", dirs);
    }

    /// <summary>在资源管理器里打开目录；没给路径就打开推荐投放目录。</summary>
    void RevealMediaDirectory(string? path)
    {
        string target = string.IsNullOrWhiteSpace(path) ? DefaultDropDirectory() : path!;
        if (target.Length == 0) return;

        try
        {
            if (!Directory.Exists(target)) Directory.CreateDirectory(target);

            // 必须写全名：using ElectronNET.API 后 Process 和 ElectronNET.API.Process 撞名（CS0104）
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + target + "\"")
                {
                    UseShellExecute = true,
                });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[WallpaperEngine] 打开目录失败：{Path}", target);
        }
    }

    /// <summary>推荐投放目录：图片\AlifeWallpaper（始终参与扫描）。</summary>
    static string DefaultDropDirectory()
    {
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures)) return "";

        string dir = Path.Combine(pictures, "AlifeWallpaper");
        try { Directory.CreateDirectory(dir); }
        catch { }

        return dir;
    }

    static bool IsTrue(string? raw)
        => raw != null && (raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase));

    static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string pair in (query ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq >= 0 ? pair.Substring(0, eq) : pair;
            string value = eq >= 0 ? pair.Substring(eq + 1) : "";

            try { result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace("+", " ")); }
            catch { }
        }

        return result;
    }

    // ---------------- AI 可调用函数 ----------------

    [XmlFunction(FunctionMode.OneShot)]
    [Description("重新扫描壁纸库：会把自己丢进「图片 / AlifeWallpaper」等目录的图片和视频一起收进来。刚放了新文件时调用一次即可。")]
    public Task RefreshWallpapers()
    {
        catalog.Refresh(Configuration);
        interactor.Poke(catalog.LastSummary);
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("切换 Alife 界面背景壁纸。query 填标题关键词或壁纸 ID；留空则使用默认/第一张。")]
    public async Task SetWallpaper([Description("壁纸标题关键词或 ID，可留空")] string query = "")
    {
        if (catalog.Count == 0) catalog.Refresh(Configuration);

        WallpaperItem? item = FindVisible(query);
        if (item == null)
        {
            interactor.Poke(catalog.Count == 0
                ? "壁纸库为空：先在 Wallpaper Engine 订阅壁纸，或在「自定义壁纸目录」里填文件夹；也可以直接把图片 / 视频丢进 图片\\AlifeWallpaper。"
                : $"没有找到匹配「{query}」的壁纸。可用壁纸：" + string.Join("、", VisibleItems().Take(10).Select(x => x.Title)));
            return;
        }

        try
        {
            await PlayAsync(item);
            interactor.Poke($"已切换壁纸：{item.Title}（{item.Type}）。");
        }
        catch (Exception ex)
        {
            interactor.Poke("切换壁纸失败：" + ex.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按标签/氛围切换壁纸（如 风景、赛博朋克、夜景、治愈、动漫）；找不到标签时按标题关键词匹配。")]
    public async Task SetWallpaperByTag([Description("标签或氛围关键词")] string tag)
    {
        if (catalog.Count == 0) catalog.Refresh(Configuration);

        // 隐藏的壁纸不参与标签挑选：用户主动藏起来的就是不想让它再被翻出来
        List<WallpaperItem> visible = VisibleItems();
        WallpaperItem? item = visible.FirstOrDefault(x =>
                                x.Tags.Any(t => t.Contains(tag, StringComparison.CurrentCultureIgnoreCase)))
                            ?? visible.FirstOrDefault(x => x.Title.Contains(tag, StringComparison.CurrentCultureIgnoreCase));

        if (item == null)
        {
            interactor.Poke(catalog.Count == 0
                ? "壁纸库为空：先在 Wallpaper Engine 订阅壁纸，或在「自定义壁纸目录」里填文件夹；也可以直接把图片 / 视频丢进 图片\\AlifeWallpaper。"
                : $"没有找到标签或标题包含「{tag}」的壁纸。可调用 ListWallpaperTags 看看有哪些标签。");
            return;
        }

        try
        {
            await PlayAsync(item);
            string tags = item.Tags.Count > 0 ? "，标签：" + string.Join("、", item.Tags) : "";
            interactor.Poke($"已切换到「{item.Title}」（{item.Type}{tags}）。");
        }
        catch (Exception ex)
        {
            interactor.Poke("切换壁纸失败：" + ex.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("切换到下一张壁纸。")]
    public Task NextWallpaper() => StepAsync(1);

    [XmlFunction(FunctionMode.OneShot)]
    [Description("切换到上一张壁纸。")]
    public Task PreviousWallpaper() => StepAsync(-1);

    [XmlFunction(FunctionMode.OneShot)]
    [Description("列出壁纸库（标题、类型、ID、标签），用于挑选要切换的壁纸。")]
    public Task ListWallpapers([Description("最多返回数量，默认 20")] int limit = 20)
    {
        if (catalog.Count == 0) catalog.Refresh(Configuration);

        if (catalog.Count == 0)
        {
            interactor.Poke("壁纸库为空。");
            return Task.CompletedTask;
        }

        limit = Math.Clamp(limit, 1, 100);

        List<WallpaperItem> visible = VisibleItems();
        string lines = string.Join("\n", visible.Take(limit)
            .Select((x, i) => $"{i + 1}. {x.Title} [{x.Type}] {x.Id}" +
                              (x.Tags.Count > 0 ? " #" + string.Join(" #", x.Tags.Take(4)) : "")));

        interactor.Poke($"壁纸库共 {visible.Count} 张：\n{lines}" +
                        (visible.Count > limit ? $"\n…（仅显示前 {limit} 张）" : ""));
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("列出壁纸库里出现过的标签（按出现次数排序），便于按氛围挑选。")]
    public Task ListWallpaperTags()
    {
        if (catalog.Count == 0) catalog.Refresh(Configuration);

        var tags = catalog.Items
            .SelectMany(x => x.Tags)
            .GroupBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(60)
            .Select(g => g.Key)
            .ToList();

        interactor.Poke(tags.Count == 0
            ? "壁纸库没有标签信息；可以直接用标题关键词调用 SetWallpaper。"
            : "可用标签：" + string.Join("、", tags));
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查询当前正在显示的壁纸。")]
    public Task CurrentWallpaper()
    {
        if (current == null)
        {
            interactor.Poke("当前没有显示壁纸。");
        }
        else
        {
            string tags = current.Tags.Count > 0 ? "，标签：" + string.Join("、", current.Tags) : "";
            interactor.Poke($"当前壁纸：{current.Title}（{current.Type}，{current.Id}{tags}）。");
        }
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("停止显示壁纸（移除 Alife 界面背景层，可随时再切换）。")]
    public async Task StopWallpaper()
    {
        await StopAsync();
        interactor.Poke("已停止显示壁纸。");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("重新扫描本机 Wallpaper Engine 壁纸库（刚订阅新壁纸后调用）。")]
    public Task RescanWallpapers()
    {
        catalog.Refresh(Configuration);
        index = current == null ? -1 : catalog.IndexOf(current);
        interactor.Poke($"已重新扫描：{catalog.LastSummary}。");
        return Task.CompletedTask;
    }
}
