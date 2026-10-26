using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.JSInterop;

namespace presents.Wallpaper;

/// <summary>
/// Alife 5.0 起主界面是 WebView2 + Blazor，插件不再拿得到 Electron 窗口，
/// 只能通过 Blazor 的 IJSRuntime 往页面里执行脚本。
///
/// 客户端会按模块的 GlobalUI 自动挂载本组件（见 GlobalUIOverlay）：
/// 挂载时把 JS 执行器交给 <see cref="AlifeBackground"/>，并重放一次注入脚本。
/// 组件本身不渲染任何内容，只当生命周期与脚本执行入口。
/// </summary>
public sealed class WallpaperGlobalUI : ComponentBase
{
    [Inject] IJSRuntime JS { get; set; } = null!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // 每次都刷新一次引用：页面重载后旧的 IJSRuntime 会失效
        AlifeBackground.JsRunner = RunJsAsync;

        try
        {
            await WallpaperModule.OnUiReadyAsync();
        }
        catch
        {
            // 主窗口还没起好：下一次渲染或下次切壁纸时会再试
        }
    }

    Task<string> RunJsAsync(string script) => JS.InvokeAsync<string>("eval", script).AsTask();
}
