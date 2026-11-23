using System;
using System.Collections.Generic;

namespace presents.Wallpaper;

/// <summary>
/// 一个 Wallpaper Engine 壁纸项目。
/// Root 是项目目录；File 是相对 Root 的入口文件（视频 mp4 / 网页 index.html / 场景 pkg）。
/// </summary>
internal sealed record WallpaperItem(
    string Id,
    string Title,
    string Type,
    string Root,
    string File,
    string Preview,
    string Source)
{
    /// <summary>project.json 里的 tags，供 AI 按风格/氛围挑选。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>video / web / image 可以直接显示；scene 暂时以预览图展示。</summary>
    public bool IsPlayable => Type is "video" or "web" or "image";

    /// <summary>用户自己丢进来的图片 / 视频（不是 Wallpaper Engine 项目）。</summary>
    public bool IsUserMedia => Source == "media";
}
