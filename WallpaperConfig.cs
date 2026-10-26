using System.ComponentModel;

namespace presents.Wallpaper;

/// <summary>插件配置。Alife 会根据这些属性自动生成模块设置表单。</summary>
public sealed class WallpaperConfig
{
    [DisplayName("启用动态壁纸")]
    [Description("模块启动时自动打开壁纸窗口。关闭后仍可由 AI 调用 SetWallpaper 唤醒。")]
    public bool AutoStart { get; set; } = true;

    [DisplayName("遮罩浓度")]
    [Description("0–100。盖在壁纸上的整层颜色，越大文字越清楚；颜色由下面「遮罩用白色」决定。")]
    public int Opacity { get; set; } = 35;

    [DisplayName("遮罩用白色")]
    [Description("关闭 = 黑色遮罩（压暗壁纸，深色氛围）；开启 = 白色遮罩（提亮柔化，浅色界面更协调）。")]
    public bool WhiteScrim { get; set; } = false;

    [DisplayName("背景模糊（px）")]
    [Description("0 = 壁纸清晰；越大越有毛玻璃感。")]
    public int Blur { get; set; } = 0;

    [DisplayName("亮度（%）")]
    [Description("100 = 原样；配合白色遮罩可以整体提亮，压暗就用 60–90。")]
    public int Brightness { get; set; } = 100;

    [DisplayName("对比度（%）")]
    [Description("100 = 原样；120–140 会让壁纸线条更硬朗。")]
    public int Contrast { get; set; } = 100;

    [DisplayName("饱和度（%）")]
    [Description("100 = 原样；0 = 黑白，150+ 更艳丽。")]
    public int Saturate { get; set; } = 100;

    [DisplayName("播放倍速（%）")]
    [Description("仅视频壁纸。50–200，100 = 1.0x 原速；150 = 1.5x，200 = 2.0x（挂件面板里直接显示成倍数）。")]
    public int PlaybackRate { get; set; } = 100;

    [DisplayName("切换过渡")]
    [Description("开启后换壁纸是淡入淡出；关闭 = 硬切。")]
    public bool FadeTransition { get; set; } = true;

    [DisplayName("磨砂白度")]
    [Description("0–100。挡住壁纸的那些白底区域（左栏 / 右栏 / 表单卡片 / 输入框）会被换成这个浓度的磨砂白；0 = 完全透出壁纸，40–60 兼顾通透与可读。")]
    public int PanelOpacity { get; set; } = 45;

    [DisplayName("气泡不透明度")]
    [Description("0–100。聊天气泡和绿底 / 彩色提示卡单独的一条透明度；100 = 保持原样不透明，0 = 完全透出壁纸。白底卡片仍由上面的「磨砂白度」管。")]
    public int BubbleOpacity { get; set; } = 65;

    [DisplayName("显示切换挂件")]
    [Description("在界面右下角显示壁纸挂件：展开可以选壁纸、上一张/下一张，并直接调遮罩 / 白度 / 模糊。")]
    public bool ShowWidget { get; set; } = true;

    [DisplayName("完整显示（留黑边）")]
    [Description("关闭 = 铺满裁切（Cover）；开启 = 完整显示（Contain）。")]
    public bool ContainFit { get; set; } = false;

    [DisplayName("场景壁纸实时渲染")]
    [Description("开启后 Scene 类型壁纸用 WebWallGL 在页面里实时渲染（粒子 / 骨骼 / 文字 / 光效）；关闭或渲染失败时退回项目预览图。单张场景约占 100–200 MB 内存。")]
    public bool LiveScene { get; set; } = true;

    [DisplayName("运行场景脚本")]
    [Description("场景壁纸 project.json 里的那些数值（用户属性）只有场景脚本读得到：关掉后画面照常，但挂件里调数值不会有任何反应。开启后壁纸自带的脚本会在本页面里执行，只对你信任的壁纸开启。")]
    public bool SceneScripts { get; set; } = true;

    [DisplayName("鼠标视差")]
    [Description("0–100。光标移动时壁纸跟着轻微位移（关于屏幕中心对称），0 = 关闭。场景壁纸还会把指针转给引擎，人物/镜头会跟着鼠标动。")]
    public int Parallax { get; set; } = 35;

    [DisplayName("视频壁纸静音")]
    [Description("默认静音；关闭后可听到壁纸自带音频。")]
    public bool Mute { get; set; } = true;

    [DisplayName("音量")]
    [Description("0–200，仅在取消静音时生效。100 = 原始音量，超过 100 走 WebAudio 增益放大（很多 Wallpaper Engine 视频本身只有 -30dB 左右，嫌小就拉到 150–200）。")]
    public int Volume { get; set; } = 100;

    [DisplayName("额外壁纸目录")]
    [Description("每行一个目录；目录下每个子文件夹含 project.json（Wallpaper Engine 项目格式）。")]
    public string ExtraDirectories { get; set; } = "";

    [DisplayName("自定义壁纸目录")]
    [Description("每行一个目录，递归扫描里面的图片 / 视频。推荐直接在挂件「我的」标签里点「＋ 文件夹」选，这里只是手工兜底。图片 jpg/png/webp/bmp/gif/avif，视频 mp4/webm/m4v/mov。")]
    public string MediaDirectories { get; set; } = "";

    [DisplayName("自动扫描图片 / 视频 / 桌面")]
    [Description("默认关闭，避免把个人目录里的成百上千张图混进壁纸列表。开启后才会扫「图片」「视频」「桌面根目录」；「图片\\AlifeWallpaper」这个投放目录始终有效。")]
    public bool ScanUserFolders { get; set; } = false;

    [DisplayName("素材递归深度")]
    [Description("1–6 层。太深等于把硬盘扫一遍，默认 3 层足够覆盖 图片/2024/旅行/xx.jpg。")]
    public int MediaDepth { get; set; } = 3;

    [DisplayName("素材数量上限")]
    [Description("最多收录多少个文件（全局），大的图优先——避免几十万张图把列表拖垮；默认 200。")]
    public int MediaLimit { get; set; } = 200;

    [DisplayName("默认壁纸")]
    [Description("启动时显示的壁纸 ID 或标题关键词；留空则取列表第一个。")]
    public string DefaultWallpaper { get; set; } = "";

    [DisplayName("自动轮播（分钟）")]
    [Description("每隔 N 分钟切换下一张；0 = 关闭。")]
    public int RotationMinutes { get; set; } = 0;

    [DisplayName("恢复上次壁纸")]
    [Description("启动时优先显示上次的壁纸；填了「默认壁纸」时以默认壁纸为准。")]
    public bool RestoreLast { get; set; } = true;

    [DisplayName("只保留可播放壁纸")]
    [Description("开启后跳过场景（.pkg）壁纸，只保留视频 / 网页，避免列表里出现只有预览图的项。")]
    public bool SkipScenes { get; set; } = false;

    [DisplayName("排除关键词")]
    [Description("标题包含这些词的壁纸会被过滤（逗号 / 分号 / 换行分隔）。")]
    public string ExcludeKeywords { get; set; } = "";

    [DisplayName("挂件形象")]
    [Description("右下角壁纸胶囊的外观：0 = 经典胶囊（图标 + 「壁纸」），1 = 大肥鱼 Q 版大头贴，2 = 大肥鱼思考气泡版。换成 Q 版后点开仍是原来的壁纸面板，位置和功能都不变。")]
    public int PetAvatar { get; set; } = 1;

    [DisplayName("挂件大小（px）")]
    [Description("Q 版形象的边长，56–120；经典胶囊不受影响。")]
    public int PetSize { get; set; } = 76;

    [DisplayName("挂件面板不透明度")]
    [Description("只管右下角这个设置面板：0 = 完全透明，能直接看到身后的壁纸（文字会加深色描边保证可读）；100 = 纯黑面板。页面里那些卡片由「白度」控制，互不影响。")]
    public int WidgetPanelOpacity { get; set; } = 55;

    [DisplayName("挂件位置 X")]
    [Description("拖动挂件后自动记录（-1 = 默认右下角）。想复原就点面板底部的「挂件归位」，或把 X / Y 都改回 -1。")]
    public int WidgetX { get; set; } = -1;

    [DisplayName("挂件位置 Y")]
    [Description("同上，-1 = 默认右下角。")]
    public int WidgetY { get; set; } = -1;
}
