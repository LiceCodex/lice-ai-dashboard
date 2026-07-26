# Lice AI Dashboard

轻量的原生 Windows 托盘小组件，用于查看 Codex 周额度、VPN/代理出口和常用
AI 服务的网络状态。

![Windows](https://img.shields.io/badge/Windows-10%2F11-0078D4)
![Version](https://img.shields.io/badge/version-1.3-6F97FF)
![Runtime](https://img.shields.io/badge/runtime-.NET%20Framework-512BD4)

## 功能

- 显示 Codex 周额度和刷新时间
- 显示 VPN/代理出口、实际 HTTPS 延迟、节点历史与服务状态
- 显示 IP 纯净度评分、风险原因，以及是否推荐用于 AI
- 原生 Windows 深色扁平化卡片界面，信息层级清晰且避免透明叠影
- 网络节点卡片支持手动刷新；切换节点后可立即重新识别出口、延迟与纯净度
- 设置中可手动开启或关闭“登录 Windows 后自动启动”
- 鼠标移动到右下角托盘图标时自动展开，移开窗口后自动收起
- 左键单击托盘图标可固定打开/收起
- 右键托盘图标可刷新或完全退出
- 无 CMD/PowerShell 常驻窗口

## 使用

从 Releases 下载 `Lice AI Dashboard.exe` 后直接运行，不需要安装 Python 或其他
第三方依赖。

程序会在本地读取已有的 `%USERPROFILE%\.codex\auth.json` 获取周额度。登录令牌
不会被输出、上传或写入项目数据。

本地配置及节点历史保存在：

```text
%LOCALAPPDATA%\LiceAIDashboard
```

## 从源码构建

Windows 10/11 自带的 .NET Framework 编译器即可完成构建：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

输出文件：

```text
dist\Lice AI Dashboard.exe
```

## 数据来源说明

- Codex 周额度依赖 ChatGPT/Codex 的非公开内部接口，未来可能发生变化。
- 公网出口信息通过 Cloudflare trace 获取。
- IP 纯净度使用公开信誉信号进行启发式评分，并按 IP 缓存 6 小时。
- 评分综合代理、Tor、机房网络及公开滥用记录；结果仅供选节点参考，不是
  OpenAI、Google 或其他平台的官方风控结论。
- 延迟采用实际 HTTPS 请求时间，兼容 Windows 系统代理及常见 VLESS 客户端。
- 节点历史仅保存在本机。
