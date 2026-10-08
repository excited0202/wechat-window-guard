# 微信窗口救援 · WeChat Window Guard

外接显示器断开后，微信仍已登录、后台能收到消息，但主窗口留在屏幕外？这个小工具把符合条件的微信主窗口移回当前屏幕，不要求重新登录。

A small, local-only Windows utility that recovers an already logged-in Weixin main window stranded on a disconnected display. No network requests, telemetry, or chat-content access.

## 功能与边界

- 自动模式约每 2 秒检查一次；显示器布局稳定、窗口位置连续两次异常时尝试修复。
- 只处理当前 Windows 会话中符合条件的微信主窗口；不启动微信、不退出账号、不读取聊天记录。
- 自动模式保留隐藏/最小化状态，不主动抢焦点；“救援”入口则主动恢复并尝试显示已有主窗口。
- 不移动仍可操作的正常窗口，跳过最大化窗口及其他虚拟桌面的隐藏窗口。
- 修复的是窗口位置异常，不是微信程序崩溃、登录问题或所有类型的白屏。

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

## 使用

| 参数 | 行为 |
| --- | --- |
| `--watch`（默认） | 后台自动修复，不主动显示微信窗口 |
| `--recover` | 救援并尝试显示已经登录的微信主窗口；未识别到返回退出码 2 |
| `--inspect` | 只读检查显示器和候选窗口信息 |
| `--stop` | 停止当前会话守护进程，保留开机启动项 |
| `--disable` | 停止并备份移出本工具的开机启动快捷方式 |
| `--self-test` | 使用合成坐标和自建临时窗口测试，不操作微信 |

第二个参数可指定 JSON 报告路径。默认运行记录保存在 `%LOCALAPPDATA%\WeChatWindowGuard`；测试脚本的报告保存在忽略提交的 `build` 目录。自动修复通常在数秒内生效，忙碌应用及桌面切换可能延后。

## 停用与移除

使用开始菜单“停用微信窗口自动修复”，或运行 `WeChatWindowGuard.exe --disable`。这会保留程序、备份及本地记录。

要彻底移除，先停用，再手动删除本工具的安装目录、桌面救援快捷方式和开始菜单“微信窗口修复”文件夹。请核对目标，不要删除微信本身的程序或数据目录。

## 隐私与贡献

程序没有联网逻辑，不访问微信账号、联系人或聊天数据。日志仍可能包含进程编号、窗口位置、屏幕尺寸和异常中的本机路径；这些信息应留在本地。详见 [隐私说明](PRIVACY.md) 和 [贡献指南](CONTRIBUTING.md)。

仓库只收录通用源码、脚本及文档；`.gitignore` 使用允许清单，默认排除新文件、日志、JSON、可执行文件和调试符号。允许清单不能替代提交前检查，也无法阻止强制添加。

## 许可

[MIT](LICENSE)。非腾讯官方项目，与腾讯/微信没有隶属或合作关系。软件按现状提供，请自行评估使用风险。
