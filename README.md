# 微信窗口救援：窗口不显示、白边与最大化越界（Windows）

**WeChat Window Guard** 是一个 Windows 小工具，用来修复“微信已经登录、后台正常运行，但主窗口跑到屏幕外”的问题。它把符合条件的微信主窗口移回当前显示器，不重启微信、不退出账号、不要求重新登录。

另提供有限兼容的“微信白边与最大化修复”手动入口，用于已验证版本中窗口边框样式异常、最大化后四角错位或底部超出屏幕的情况。它与原自动守护分开使用，不是所有白边、白屏或显示故障的通用修复方案。

## 你是不是遇到了这些情况？

- 笔记本连接外接显示器、双屏或扩展屏后，拔掉显示器，微信窗口打不开或不显示。
- 微信后台还在运行，仍能收到消息，但点击任务栏或托盘图标，主窗口就是出不来。
- `Alt+Tab` 能找到微信，桌面上却看不到窗口；窗口像是跑到了屏幕外。
- 用分屏、贴靠窗口或切换显示模式能暂时找回来，下次断开外接屏又复发。

如果问题是“断开显示器后，微信记住了已不存在屏幕上的窗口位置”，本工具可以帮助恢复，并在后台自动检查复发情况。

如果微信进程未运行、程序崩溃、账号未登录、登录二维码异常或白屏，本工具并不是对应的通用修复方案。即便现象相似，也应先确认是不是窗口位置问题。

A small, local-only Windows utility for WeChat / Weixin window not showing after disconnecting an external monitor: the app is still running, but its main window is off-screen. Recovers an already logged-in window without restarting or signing in again. No network requests, telemetry, or chat-content access.

A separate, version-gated manual recovery mode handles recognized white-border and maximized-window overflow states. It does not promise permanent prevention or compatibility with all WeChat versions.

## 功能与边界

- 自动模式约每 2 秒检查一次；显示器布局稳定、窗口位置连续两次异常时尝试修复。
- 只处理当前 Windows 会话中符合条件的微信主窗口；不启动微信、不退出账号、不读取聊天记录。
- 自动模式保留隐藏/最小化状态，不主动抢焦点；“救援”入口则主动恢复并尝试显示已有主窗口。
- 自动模式不移动仍可操作的正常窗口，跳过最大化窗口及其他虚拟桌面的隐藏窗口。
- 修复的是窗口位置异常，不是微信程序崩溃、登录问题或所有类型的白屏。

### 手动修复白边 / 最大化底部超出屏幕（有限兼容）

另有独立的手动入口，针对微信小窗出现明显白边、最大化后四角错位或底部越界的一种特定故障：主窗口原有的边框样式丢失。当前仅接受已验证的微信 **4.1.15.13**、窗口类 `Qt51514QWindowIcon` 和精确匹配的异常样式及渲染状态，并交叉核对已加载的客户端模块版本。其他版本、不同故障或多个候选窗口会拒绝修改。

此入口不自动运行、不退出微信、不清除登录，也不读取聊天内容。它恢复边框样式，必要时校正当前最大化窗口的工作区边界，随后回读检查。识别到正常窗口时不修改；窗口最小化、正在拖动、忙碌或显示器布局发生变化时停止。失败后仅在状态仍可确认时尝试回退，无法回退会明确提示。

手动边框模式识别两种已核对的渲染子窗口类别：`MMUIRenderSubWindowHW` 与 `MMUIRenderSubWindow`。这不放宽版本、主窗口类、异常样式或 DWM 状态限制，也不改变自动守护的识别规则。

此入口也会检查“伪最大化”：窗口看似小窗，系统却仍标记为最大化，实际尺寸与所在屏幕工作区不一致。边框参数完整时，工具会单独提示这种状态，不会误报正常，也不会强行改边框或把小窗撑满屏幕。此时先点一次微信右上角“还原”，再查看实际显示。技术参数正常或窗口截图没有边，不等于实际屏幕上一定没有白边。

这是故障发生后的恢复手段，**尚不能证明根因已永久修复**。自动化测试包含 57 项检查，使用合成数据和工具自建窗口；另已在符合上述版本及状态的实际异常窗口上验证恢复，并完成“还原后再次最大化”的复测。一次成功恢复不代表其他版本、配置或未来复发都能同样处理。

目前针对 64 位 Windows 10 1703 及更新版本 / Windows 11，以及默认安装位置 `%ProgramFiles%\Tencent\Weixin\Weixin.exe` 的 Qt 版微信主窗口。不同微信版本可能改变窗口类名；旧版 `WeChat.exe`、非默认安装路径和登录界面不在当前识别范围内。请先使用 `--inspect` 在本地检查能否识别，不要默认认为所有版本兼容。

## 构建与安装

无需管理员权限，不下载依赖；使用 Windows 自带的 .NET Framework C# 编译器。PowerShell 在项目目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

安装会在当前用户的本地应用目录保存程序，建立开机启动、桌面“微信窗口救援”和开始菜单停用入口。已有同路径安装会先停止守护进程并备份旧程序；不会关闭微信。第三行会改变本机安装和启动项，前两行仅构建与测试。

如只想临时使用，可构建后运行：

```powershell
.\build\WeChatWindowGuard.exe --recover
```

仅安装“微信白边与最大化修复”手动入口、保留现有守护程序和开机启动项不变：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -FrameRepairOnly
```

这会另存 `WeChatWindowFrameRepair.exe`，在桌面和开始菜单建立手动快捷方式；安装本身不会操作微信或启动新守护进程。异常再出现时，先打开已登录的聊天主窗口，再双击此入口。不要连续重复点击；若提示版本不支持或回退失败，请保留本地状态并正常重启微信。

`build.ps1` / `test.ps1` 可用 `-OutputDirectory` 指定绝对输出目录；安装脚本可用 `-BuildOutputDirectory` 指定构建目录，用 `-InstallDirectory` 指定本工具安装目录。不要将安装目录指向微信本身或其他应用目录。

## 使用

| 参数 | 行为 |
| --- | --- |
| `--watch`（默认） | 后台自动修复，不主动显示微信窗口 |
| `--recover` | 救援并尝试显示已经登录的微信主窗口；未识别到返回退出码 2 |
| `--inspect` | 只读检查显示器和候选窗口信息 |
| `--inspect-frame` | 只读检查主窗口边框、DWM 边界和客户端版本 |
| `--repair-frame-ui` | 手动修复已识别的白边 / 最大化异常，并显示结果 |
| `--repair-frame` | 同上，但只写本地报告，不显示结果对话框 |
| `--stop` | 停止当前会话守护进程，保留开机启动项 |
| `--disable` | 停止并备份移出本工具的开机启动快捷方式 |
| `--self-test` | 使用合成坐标和自建临时窗口测试，不操作微信 |

第二个参数可指定 JSON 报告路径。默认运行记录保存在 `%LOCALAPPDATA%\WeChatWindowGuard`；测试脚本的报告保存在忽略提交的 `build` 目录。自动修复通常在数秒内生效，忙碌应用及桌面切换可能延后。

边框检查与修复报告单独保存在上述目录下的 `FrameRepair` 子目录。`WeChatWindowFrameRepair.exe` 不带参数时默认打开手动修复模式，不会启动自动守护；原 `WeChatWindowGuard.exe` 的默认行为仍为 `--watch`。

## 停用与移除

使用开始菜单“停用微信窗口自动修复”，或运行 `WeChatWindowGuard.exe --disable`。这会保留程序、备份及本地记录。

要彻底移除，先停用，再手动删除本工具的安装目录、桌面救援及白边修复快捷方式和开始菜单“微信窗口修复”文件夹。若只移除手动边框修复入口，删除 `WeChatWindowFrameRepair.exe`、`FrameRepair-README.md`、`FrameRepair-PRIVACY.md` 和对应两个快捷方式即可；保留原守护程序。请核对目标，不要删除微信本身的程序或数据目录。

## 隐私与贡献

程序没有联网逻辑，不访问微信账号、联系人或聊天数据。日志仍可能包含进程编号、窗口位置、屏幕尺寸和异常中的本机路径；这些信息应留在本地。详见 [隐私说明](PRIVACY.md) 和 [贡献指南](CONTRIBUTING.md)。

仓库只收录通用源码、脚本及文档；`.gitignore` 使用允许清单，默认排除新文件、日志、JSON、可执行文件和调试符号。允许清单不能替代提交前检查，也无法阻止强制添加。

## 常见检索关键词

微信窗口打不开、微信窗口不显示、微信后台运行但界面打不开、点击微信任务栏没有反应、微信窗口跑到屏幕外、拔掉外接显示器微信消失、双屏切回单屏微信打不开、多显示器窗口位置恢复。

有限兼容的手动边框恢复：微信窗口白边、微信最大化超出屏幕、微信底部被遮挡、微信最大化边界错位、微信伪最大化、WeChat white border、Weixin maximized window overflow。

English: WeChat window not showing; Weixin window invisible; WeChat running in background but won't open; off-screen window; external monitor disconnected; multi-monitor window recovery; Windows DPI.

## 许可

[MIT](LICENSE)。非腾讯官方项目，与腾讯/微信没有隶属或合作关系。软件按现状提供，请自行评估使用风险。
