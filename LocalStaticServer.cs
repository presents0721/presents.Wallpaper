using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace presents.Wallpaper;

/// <summary>
/// 极小的只读本地静态服务（TcpListener 手写 HTTP/1.1）：
///   /      → 插件的 wwwroot（壁纸渲染页）
///   /wp/   → 当前壁纸项目目录（视频 / 网页 / 预览图）
/// 只绑定 127.0.0.1 随机端口，支持 Range，视频可正常拖动与分段加载。
/// 不用 HttpListener 是为了避免 URL ACL / 管理员权限。
/// </summary>
internal sealed class LocalStaticServer : IDisposable
{
    static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".avif"] = "image/avif",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
        [".ogv"] = "video/ogg",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".wasm"] = "application/wasm",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".txt"] = "text/plain; charset=utf-8",
    };

    readonly TcpListener listener;
    readonly CancellationTokenSource cts = new();
    readonly string webRoot;
    readonly object gate = new();
    string? wallpaperRoot;

    public int Port { get; }
    public string BaseUrl => "http://127.0.0.1:" + Port + "/";

    /// <summary>处理 /api/* 请求：(路径, query) → JSON 字符串。由插件模块挂上去。</summary>
    public Func<string, string, Task<string>>? ApiHandler { get; set; }

    /// <summary>把壁纸 ID 映射到预览图绝对路径，供挂件列表显示缩略图；找不到返回 null。</summary>
    public Func<string, string?>? PreviewResolver { get; set; }

    /// <summary>
    /// 网页壁纸的用户属性（JSON）。/wph/ 下发的 HTML 会把它当种子写进页面，
    /// 壁纸加载完就能拿到用户改过的值，而不是干等 postMessage。
    /// </summary>
    public Func<string>? WebPropsProvider { get; set; }

    public LocalStaticServer(string webRoot)
    {
        this.webRoot = Path.GetFullPath(webRoot);

        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>切换当前壁纸项目目录；传空表示不暴露 /wp/。</summary>
    public void SetWallpaperRoot(string? root)
    {
        lock (gate)
            wallpaperRoot = string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);
    }

    async Task AcceptLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cts.Token); }
            catch { break; }
            _ = Task.Run(() => Handle(client));
        }
    }

    async Task Handle(TcpClient client)
    {
        try
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                client.ReceiveTimeout = 20000;
                client.SendTimeout = 180000;

                string? head = ReadHead(stream);
                if (head is null) return;

                string[] lines = head.Split("\r\n");
                string[] first = lines[0].Split(' ');
                if (first.Length < 2) return;

                string method = first[0].ToUpperInvariant();
                string target = first[1];
                string range = "";
                foreach (string line in lines)
                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                        range = line.Substring(6).Trim();

                string query = "";
                int queryIndex = target.IndexOf('?');
                if (queryIndex >= 0)
                {
                    query = target.Substring(queryIndex + 1);
                    target = target.Substring(0, queryIndex);
                }
                try { target = Uri.UnescapeDataString(target); } catch { }

                // 跨域预检（挂件跑在 Alife 页面的 origin 里）
                if (method == "OPTIONS")
                {
                    WriteJson(stream, "{}", "204 No Content");
                    return;
                }

                if (target.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && ApiHandler != null)
                {
                    string json;
                    try { json = await ApiHandler(target, query); }
                    catch (Exception ex) { json = "{\"ok\":false,\"error\":" + System.Text.Json.JsonSerializer.Serialize(ex.Message) + "}"; }
                    WriteJson(stream, json);
                    return;
                }

                string? file = target.StartsWith("/pv/", StringComparison.OrdinalIgnoreCase) && PreviewResolver != null
                    ? PreviewResolver(Uri.UnescapeDataString(target.Substring(4)))
                    : Resolve(target);
                if (file is null || !File.Exists(file))
                {
                    WriteText(stream, "404 Not Found", "no such file: " + target);
                    return;
                }

                // 网页壁纸：/wph/ 下发的入口 HTML 要带上属性种子 + 接收端（跨源 iframe 碰不到它的 document）
                if (method != "HEAD" && target.StartsWith("/wph/", StringComparison.OrdinalIgnoreCase) &&
                    (file.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)))
                {
                    byte[] page;
                    try { page = Encoding.UTF8.GetBytes(InjectWebProps(File.ReadAllText(file))); }
                    catch { page = File.ReadAllBytes(file); }

                    string injectHead =
                        "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: text/html; charset=utf-8\r\n" +
                        "Content-Length: " + page.Length + "\r\n" +
                        "Access-Control-Allow-Origin: *\r\n" +
                        "Cache-Control: no-store\r\n" +
                        "Connection: close\r\n\r\n";

                    byte[] hb = Encoding.ASCII.GetBytes(injectHead);
                    stream.Write(hb, 0, hb.Length);
                    stream.Write(page, 0, page.Length);
                    stream.Flush();
                    return;
                }

                FileInfo info = new(file);
                long start = 0;
                long end = info.Length - 1;
                bool partial = false;

                if (range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    string spec = range.Substring(6).Split(',')[0].Trim();
                    int dash = spec.IndexOf('-');
                    if (dash >= 0)
                    {
                        string left = spec.Substring(0, dash);
                        string right = spec.Substring(dash + 1);

                        if (left.Length == 0 && right.Length > 0 && long.TryParse(right, out long suffix))
                        {
                            start = Math.Max(0, info.Length - suffix);
                        }
                        else
                        {
                            if (left.Length > 0) long.TryParse(left, out start);
                            if (right.Length > 0 && long.TryParse(right, out long parsedEnd))
                                end = Math.Min(end, parsedEnd);
                        }
                        partial = true;
                    }
                }

                if (start > end || start >= info.Length)
                {
                    WriteText(stream, "416 Range Not Satisfiable", "bad range");
                    return;
                }

                long length = end - start + 1;
                string mime = Mime.TryGetValue(Path.GetExtension(file), out string? known)
                    ? known
                    : "application/octet-stream";

                string header =
                    (partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n") +
                    "Content-Type: " + mime + "\r\n" +
                    "Content-Length: " + length + "\r\n" +
                    // 页面在 Alife 的 origin 里，fetch 场景包/资源是跨源请求，静态文件也得放行
                    "Access-Control-Allow-Origin: *\r\n" +
                    "Accept-Ranges: bytes\r\n" +
                    (partial ? "Content-Range: bytes " + start + "-" + end + "/" + info.Length + "\r\n" : "") +
                    "Cache-Control: no-store\r\n" +
                    "Connection: close\r\n\r\n";

                byte[] headerBytes = Encoding.ASCII.GetBytes(header);
                stream.Write(headerBytes, 0, headerBytes.Length);

                if (method != "HEAD")
                {
                    using FileStream fileStream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                    fileStream.Seek(start, SeekOrigin.Begin);

                    byte[] buffer = new byte[1 << 20];
                    long remaining = length;
                    while (remaining > 0)
                    {
                        int want = (int)Math.Min(buffer.Length, remaining);
                        int got = fileStream.Read(buffer, 0, want);
                        if (got <= 0) break;
                        stream.Write(buffer, 0, got);
                        remaining -= got;
                    }
                }

                stream.Flush();
            }
        }
        catch
        {
            // 客户端断开 / 超时都属正常
        }
    }

    string? Resolve(string target)
    {
        // /wp/ 原样直出；/wph/ 与 /wp/ 同一目录，但 .html 会被注入属性种子
        bool injected = target.StartsWith("/wph/", StringComparison.OrdinalIgnoreCase);

        if (injected || target.StartsWith("/wp/", StringComparison.OrdinalIgnoreCase))
        {
            lock (gate)
            {
                if (wallpaperRoot == null) return null;
                return SafeJoin(wallpaperRoot, target.Substring(injected ? 5 : 4));
            }
        }

        return SafeJoin(webRoot, target.TrimStart('/'));
    }

    /// <summary>
    /// 把「属性种子 + 接收端」塞进入口 HTML。
    /// 网页壁纸按 WE 的约定从 window.wallpaperPropertyListener.applyUserProperties 取值，
    /// 但 iframe 是跨源的，父页面只能 postMessage，所以接收端必须写进页面里。
    /// </summary>
    string InjectWebProps(string html)
    {
        string props = "{}";
        try
        {
            string fromHost = WebPropsProvider?.Invoke() ?? "";
            if (!string.IsNullOrWhiteSpace(fromHost)) props = fromHost;
        }
        catch { }

        // 防止 JSON 里的 </script> 提前闭合
        string seed = "<script>(function(){var P=window.__ALIFE_WP_PROPS__=" + props.Replace("</", "<\\/") + ";" + WebBridge + "})();</script>";

        int at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return at >= 0 ? html.Insert(at, seed) : html + seed;
    }

    /// <summary>接收端：劫持 wallpaperPropertyListener 的赋值 + 监听父页面 postMessage。</summary>
    const string WebBridge = @"
      var holder=null, pending=null;
      try{holder=window.wallpaperPropertyListener||null;}catch(e){}
      function fire(p){ if(!holder||typeof holder.applyUserProperties!=='function')return false; try{holder.applyUserProperties(p);}catch(e){} return true; }
      function flush(){ if(pending&&fire(pending))pending=null; }
      function later(){ pending=P; flush(); setTimeout(flush,300); setTimeout(flush,1200); }
      try{Object.defineProperty(window,'wallpaperPropertyListener',{configurable:true,get:function(){return holder;},set:function(v){holder=v;later();}});}catch(e){}
      window.addEventListener('message',function(ev){var d=ev.data;if(!d||d.__alifeWp!==1||!d.props)return;for(var k in d.props){if(Object.prototype.hasOwnProperty.call(d.props,k))P[k]=d.props[k];}fire(d.props);});
      if(document.readyState!=='complete'){window.addEventListener('load',flush);}
      later();
    ";

    static string? SafeJoin(string root, string relative)
    {
        relative = relative.Replace('/', Path.DirectorySeparatorChar);
        if (relative.Length == 0) relative = "index.html";

        string full;
        try { full = Path.GetFullPath(Path.Combine(root, relative)); }
        catch { return null; }

        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        return File.Exists(full) ? full : null;
    }

    static string? ReadHead(NetworkStream stream)
    {
        MemoryStream buffer = new();
        byte[] one = new byte[1];

        while (buffer.Length < 65536)
        {
            int read;
            try { read = stream.Read(one, 0, 1); }
            catch { return null; }
            if (read <= 0) return null;

            buffer.WriteByte(one[0]);
            if (buffer.Length >= 4)
            {
                byte[] data = buffer.GetBuffer();
                int length = (int)buffer.Length;
                if (data[length - 4] == 13 && data[length - 3] == 10 &&
                    data[length - 2] == 13 && data[length - 1] == 10)
                    return Encoding.UTF8.GetString(data, 0, length);
            }
        }

        return null;
    }

    static void WriteJson(NetworkStream stream, string json, string status = "200 OK")
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        string head =
            "HTTP/1.1 " + status + "\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Headers: *\r\n" +
            "Access-Control-Allow-Methods: GET,POST,OPTIONS\r\n" +
            "Cache-Control: no-store\r\n" +
            "Content-Length: " + bytes.Length + "\r\n" +
            "Connection: close\r\n\r\n";

        byte[] header = Encoding.ASCII.GetBytes(head);
        stream.Write(header, 0, header.Length);
        if (status != "204 No Content")
            stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    static void WriteText(NetworkStream stream, string status, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string head = "HTTP/1.1 " + status + "\r\nContent-Type: text/plain; charset=utf-8\r\n" +
                      "Content-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n";
        byte[] header = Encoding.ASCII.GetBytes(head);
        stream.Write(header, 0, header.Length);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    public void Dispose()
    {
        try { cts.Cancel(); } catch { }
        try { listener.Stop(); } catch { }
        try { cts.Dispose(); } catch { }
    }
}
