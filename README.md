# presents.Wallpaper · 壁纸

把 Wallpaper Engine 的壁纸铺到 Alife 聊天界面背后：自动扫描 Steam 创意工坊（431960）与本地 `myprojects` / `defaultprojects`，视频、网页、场景（WebWallGL 实时渲染）都能用。右下角挂件可拖到任意位置，选图、上一张 / 下一张，并单独调遮罩、磨砂白度、模糊、面板不透明度；AI 也能按关键词 / 标签切壁纸。

- **自己的素材**（5.7.2 起）：挂件第三个标签「我的」里点「＋ 文件夹」选任意目录，递归扫描图片（jpg / png / webp / bmp / gif / avif）与视频（mp4 / webm / m4v / mov）。静态图与 GIF 直接当壁纸，视频循环播放；音量 0–200%、倍速 50–200% 可单独调。没有 Wallpaper Engine 也能用。
- 需要本机装过 Wallpaper Engine，或把含 `project.json` 的目录填进「额外壁纸目录」；壁纸文件本身不随插件分发。
- 场景壁纸实时渲染单张约占 100–200 MB 内存，可在设置里关闭。
- 壁纸项目里的脚本默认执行（「运行场景脚本」），只对你信任的壁纸开启。

## 安装

解压后目录名必须与插件 id 一致（`presents.Wallpaper`），放到 Alife 的 `Storage/Plugins/` 下，重启后由客户端自行编译加载。依赖 `Alife.Function.FunctionCaller`。

## 版本

当前版本 **5.8.1**，发布包见 [Releases](https://github.com/presents0721/presents.Wallpaper/releases)。

插件大版本与 Alife 大版本对应：5.x 只给 Alife 5.0.0+ 用，4.x 请勿安装或更新到 5.x（两条线功能一致，底层接口按版本适配）。

## 源码

仓库结构即插件目录结构：`manifest.json` + `*.cs` + `Resources/`。运行时产生的 `out/`、`state.json` 等不进仓库（见 `.gitignore`）。
