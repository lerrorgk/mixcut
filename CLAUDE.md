# MixCut 开发说明

## 当前目标

这是用户个人使用的 Windows 视频工具。优先保证实际流程正确、操作省事、处理高效；不要主动扩大为商业化产品、跨平台工程、通用框架或大量兼容与兜底分支。按明确需求修改并完成必要验证。

## 项目与入口

- C# / .NET 8 / WPF，CommunityToolkit.Mvvm，EF Core + SQLite。
- `src/MixCut/Models`：数据实体；`ViewModels`：界面状态；`Views`：XAML与交互；`Services`：分析、翻译、配音和导出；`Utilities/AppPaths.cs`：数据目录。
- 素材全局共享，项目通过 ProjectVideo 关联；从单个项目移除素材不能影响仍引用它的其他项目。
- 视频翻译与视频混剪是独立入口。翻译直接处理完整视频，无需项目或分镜；导出移除整条音轨，成片完全无声，不做配音或人声分离。
- 预览使用 `FfmpegFramePlayer` / `InlineVideoPlayer`，媒体处理使用内置 FFmpeg。复用现有服务与组件。

## 本机构建与验证

工作目录以当前会话实际路径为准。本机 SDK 在工作区旁 `.mixcut-tools/dotnet/dotnet.exe`；不要依赖旧机器用户目录或 PATH 中仅有运行时的 dotnet。

在仓库根目录运行：

```powershell
$dotnet = Join-Path (Split-Path $PWD -Parent) '.mixcut-tools/dotnet/dotnet.exe'
& $dotnet test src/MixCut.Tests/MixCut.Tests.csproj --no-restore -nodeReuse:false -p:UseSharedCompilation=false
& $dotnet publish src/MixCut/MixCut.csproj -c Release -r win-x64 --self-contained true -nodeReuse:false -p:UseSharedCompilation=false -o publish
```

- 构建后台运行；约一分钟无输出时检查任务与CPU，确认是否仍推进。不要全局杀其他 dotnet 或用户程序，也不要默认清空 bin/obj。
- 修改后先跑相关现有测试；修复具体问题时补最小回归。导出变动用本地生成的短素材核对帧数、时长、声音与字幕，不调用付费API做普通回归。
- 构建通过后再验证受影响的界面和日志。若无法完成某项验证，如实说明。
- 本地 publish 是开发产物；远端 push、Release、安装包发布需另有明确授权。

## 修改约定

- 先读目标文件和调用方；保留拖拽、右键、多选、撤销、播放、边界编辑和项目切换等相邻能力。
- 真正切项目时重置局部状态；同项目数据刷新保留筛选和有效勾选，避免打断预览。
- 长耗时文件复制、识别、媒体处理放后台；界面进度通过现有机制更新。
- 数据访问按现有模块使用短生命周期 DbContext；不要为小修改引入新架构。
- 共享缩略图缓存用 `ThumbnailCache.Shared`，按显示尺寸解码。
- 播放点击启动，hover仅高亮。首帧就绪前保留缩略图；播放墙钟从首帧到达起算；整片预览传入已知时长。
- 帧范围按 `[StartFrame, EndFrame)`；源帧率用于裁剪计算，拼接的中间片必须使用统一输出帧率与时间基。画面替换后原音频时间仍属于原视频，分别传递。
- 翻译字幕保留语言标点及人工换行；重新翻译部分字幕不覆盖其余校对。
- 外部媒体子进程沿用 `ChildProcessTracker`。已有错误、取消和状态处理按任务需要保留。
- 日志使用已有 ILogger/Serilog；关键变动记录可核对的计数或状态，不写密钥。

## 媒体处理已有经验

- 缩略图和场景检测不要顺手加入硬件解码参数；其滤镜和输出属于软件处理。
- 不用 `trim=start_frame` 为预览深位置seek从头解码；预览定位用输入级 `-ss`，导出保留帧精确剪裁。
- 不通过同步 `UpdateLayout()`解决卡片播放器初始尺寸；用布局完成的host尺寸预置解码目标。
- Toast浮层父容器不能全局禁用命中测试，否则撤销按钮也不可点。无背景区域可自然穿透。
