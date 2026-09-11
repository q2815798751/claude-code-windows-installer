# Claude Code Windows 一键安装包

给 Windows 电脑用的 Claude Code 单文件安装器。双击一个 exe，装完就能在桌面点图标启动。

内置 Anthropic 官方发布的 Windows 原生二进制（当前 `v2.1.268`），**安装过程完全离线**，
不需要 Node.js、不需要 npm、不需要管理员权限、不改动任何系统级设置。

```
ClaudeCodeSetup.exe   约 95 MB
├── 官方 v2.1.268 Windows 二进制（已内置）
└── 安装时无需联网下载
```

---

## 快速开始

1. 到 [Releases](../../releases) 下载 `ClaudeCodeSetup.exe`
2. **校验完整性**（见下节）
3. 双击运行
4. 安装器会先做一遍环境预检并列出结果，确认无误后点「安装」
5. 装完双击桌面「Claude Code」图标即可启动

首次启动 Claude Code 需要登录或配置 API Key，按终端里的提示操作即可。

### 校验下载的文件

安装包体积较大，建议先核对 SHA256 再运行。哈希值随 Release 说明一同公布：

```powershell
Get-FileHash .\ClaudeCodeSetup.exe -Algorithm SHA256
```

```cmd
certutil -hashfile ClaudeCodeSetup.exe SHA256
```

安装器本身也会在启动时做两次自校验：先比对**内置载荷**的 SHA256 与官方
`SHASUMS256.txt`，解压后再比对**解压产物** `claude.exe` 的 SHA256。任一不符都会拒绝安装。

---

## 安装器做了什么

### 第一步：环境预检（只读，不做任何改动）

| 检查项 | 说明 | 不通过时 |
|---|---|---|
| 安装包完整性 | 定位内置载荷并核对官方 SHA256 | **阻断** |
| 操作系统 | 需 Windows 10 1809 (build 17763) 或更高 | **阻断** |
| CPU 架构 | x64 用内置载荷；ARM64 走在线下载 | ARM64 无网则**阻断** |
| 磁盘空间 | 目标卷需 ≥ 700 MB 可用 | **阻断** |
| 网络连接 | 探测 `api.anthropic.com` / `github.com`，并做一次真实 HTTPS 请求 | 仅警告 |
| Git for Windows | 检测 Git Bash 并写入 `CLAUDE_CODE_GIT_BASH_PATH` | 仅警告 |
| 已有安装 | 扫 PATH、npm 全局目录、本安装目录 | 仅警告 |
| 进程占用 | 是否有 `claude` 正在运行 | 仅警告 |
| 长路径支持 | 读取 `LongPathsEnabled` | 仅提示 |
| 杀毒实时防护 | 提示可能拖慢解压 | 仅提示 |

预检全部完成后会给出**安装摘要**（版本、架构、安装位置、占用空间、载荷校验结果、
将要发生的全部环境变更），由你决定是否继续。

### 第二步：安装

1. 从内置载荷解出 `claude.exe`，校验 SHA256 后原子落盘
2. 实跑一次 `claude --version` 做冒烟测试
3. 写入用户级环境变量
4. 创建桌面与开始菜单快捷方式
5. 落地卸载器与卸载注册项

任一步失败都会**自动回滚**已做的系统改动。

### 第三步：完整性验证（自动执行）

安装完立即逐项核验并显示结果：二进制哈希、PATH 是否写入且无重复、用新 PATH 实际启动
`claude --version`、两个快捷方式、启动器、卸载器、卸载注册表项、Git Bash 变量。

---

## 安装位置与产生的变更

采用**每用户安装**，全程不弹 UAC：

```
%LOCALAPPDATA%\Programs\ClaudeCode\
├── claude.exe          官方原生二进制 (211 MB)
├── launch.cmd          启动器
├── Uninstall.exe       卸载器
├── manifest.json       版本 / 哈希 / 安装时间
└── install.log         完整安装日志

桌面\Claude Code.lnk
开始菜单\Claude Code\Claude Code.lnk
```

| 变更 | 位置 |
|---|---|
| 新增安装目录 | `%LOCALAPPDATA%\Programs\ClaudeCode` |
| 用户级 PATH 前置该目录 | `HKCU\Environment\Path`（保留原有类型与顺序） |
| `CLAUDE_CODE_GIT_BASH_PATH` | `HKCU\Environment`（仅在找到 Git Bash 时写入） |
| 桌面 / 开始菜单快捷方式 | 用户目录内 |
| 卸载注册项 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\ClaudeCode` |

**不会**修改系统级 PATH、**不会**改动其他软件的 PATH 条目、**不会**安装第三方软件。

### 启动方式

快捷方式优先使用 Windows Terminal（`wt.exe`），未安装则回退到 `cmd.exe`，
两者都指向安装目录里的 `launch.cmd`。`launch.cmd` 使用全路径调用 `claude.exe`，
因此**即使环境变量尚未刷新也能正常启动**。

---

## 卸载

任选其一：

- 设置 → 应用 → 已安装的应用 → 「Claude Code」→ 卸载
- 控制面板 → 程序和功能
- 直接运行 `%LOCALAPPDATA%\Programs\ClaudeCode\Uninstall.exe`

卸载会移除程序文件、PATH 条目、两个快捷方式和注册表项。

**用户配置默认保留**：`%USERPROFILE%\.claude`（登录凭据、历史、项目数据）不会被删除，
除非在卸载界面主动取消勾选保留项。不可逆操作不做默认行为。

---

## 常见问题

**装完提示找不到 Git Bash**
Claude Code 在 Windows 上依赖 Git Bash 执行命令。缺了它部分功能不可用。补齐：

```cmd
winget install Git.Git
```

或到 <https://git-scm.com/download/win> 下载。装完重新运行安装器即可自动写入
`CLAUDE_CODE_GIT_BASH_PATH`。

**预检显示 github.com 不可达**
不影响安装——x64 版用的是内置载荷，全程离线。只有 ARM64 机器需要联网从 GitHub
拉取对应二进制。

**预检显示 TLS 握手异常**
可能存在代理或证书拦截。安装本身不受影响（载荷已内置），但 Claude Code 运行时
必须能访问 `api.anthropic.com`，请确认代理设置。

**ARM64 机器（Surface Pro X 等）**
安装器会自动识别架构，从官方 Release 下载 `claude-win32-arm64.zip`（约 96 MB）
并校验官方 SHA256 后安装。受 GitHub 线路影响可能需要几分钟，进度条会显示实时速率。

**企业环境 / 有代理**
安装器走系统代理设置。若使用需要认证的代理，请先配置好 `HTTP_PROXY` / `HTTPS_PROXY`。

---

## 从源码构建

构建链**零第三方依赖**——只用 Windows 自带的 .NET Framework 编译器，不需要
NSIS / Inno Setup / 7-Zip。

```cmd
:: 1. 拉取官方载荷（约 95 MB）
powershell -ExecutionPolicy Bypass -File build\fetch-payload.ps1

:: 2. 构建
build\build.bat

:: 3. 端到端测试
build\test.bat            :: 沙箱安装 + 卸载
build\test.bat /full      :: 额外测试 ARM64 在线下载分支
```

产物在 `dist\ClaudeCodeSetup.exe`。

> 构建脚本刻意保持纯 ASCII：cmd.exe 会破坏非 ASCII 批处理文件，Windows
> PowerShell 5.1 也会误解无 BOM 的 UTF-8 脚本。中文只出现在 `.cs` 源文件中，
> 由 `csc /codepage:65001` 处理。

### 目录结构

```
├── VERSION                 当前锁定的官方版本号
├── src/
│   ├── Program.cs          入口：GUI / --selftest / --silent-install / --silent-uninstall
│   ├── MainForm.cs         安装器界面
│   ├── Checks.cs           十项预检
│   ├── Installer.cs        安装 / 验证 / 卸载核心
│   ├── Uninstaller.cs      卸载器
│   ├── Common.cs           载荷定位、哈希、路径、快捷方式、网络探测
│   ├── BuildInfo.cs        构建期生成（版本与哈希）
│   └── app.manifest        asInvoker + Win10 兼容 + DPI 感知
├── build/
│   ├── build.bat           构建主流程
│   ├── build.ps1           哈希校验 / 打包 / 自检
│   ├── fetch-payload.ps1   拉取官方载荷
│   └── test.bat            端到端测试
└── payload/                官方载荷（不入版本库）
```

### 命令行开关

```
ClaudeCodeSetup.exe --selftest                     只跑预检并打印，不做任何改动
ClaudeCodeSetup.exe --silent-install               无界面安装
ClaudeCodeSetup.exe --silent-uninstall             无界面卸载（默认保留 ~/.claude）
ClaudeCodeSetup.exe --silent-uninstall --delete-config
Uninstall.exe /quiet                               供系统「应用」列表调用
```

---

## 技术设计

### 单文件是怎么合成的

```
ClaudeCodeSetup.exe
┌──────────────────────────────────────────┐
│ [0 .. N)   C# WinForms 引导程序 (107 KB) │
│ [N .. M)   官方 claude-win32-x64.zip     │
│ 末尾 8 B   载荷长度 (Int64 LE)            │
│ 末尾 16 B  魔术字 "CLDCDSETUP-TRAIL"      │
└──────────────────────────────────────────┘
```

运行时读取自身末尾 24 字节拿到魔术字与长度，用自定义的 `SubStream` 让
`ZipArchive` **直接在 exe 尾部原地读取 zip**，不产生 95 MB 的临时副本。
卸载器仅 48 KB，作为托管资源嵌入引导程序。

### 为什么每用户安装

不请求管理员权限，从根上消除一整类安装失败（UAC 拒绝、企业策略限制、
受限账户）。代价仅是安装位置在 `%LOCALAPPDATA%` 而非 `Program Files`。

### 为什么不用 NSIS / Inno Setup

两者都不在本机，且引入外部构建依赖会让"重新构建"变得脆弱。
.NET Framework 4.x 的 `csc.exe` 是 Windows 10 自带的，配合
`/win32manifest` 和 `/resource` 足以做出完整的图形安装器，构建结果完全可复现。

### 两个踩过的坑

- **`SslStream.AuthenticateAsClient(host)` 不带协议参数时只用 TLS 1.0**，
  现代服务器一律拒绝，报"要求的函数不受支持"。必须显式传协议集。
- **`SslStream` 握手没有超时参数**。对"端口通但握手不回应"的对端会挂到系统
  默认超时（约 21 秒）。必须用独立线程 + 看门狗兜底。

---

## 版本与来源

| 项目 | 值 |
|---|---|
| Claude Code 版本 | `2.1.268`（2026-09-10 发布） |
| 来源 | <https://github.com/anthropics/claude-code/releases/tag/v2.1.268> |
| 载荷 | `claude-win32-x64.zip` |
| 官方 SHA256 | `50f44379a30cb8bac0d238654e140f5602c32ab6a2a98e80f649a8bcf35c41f9` |
| 解压产物 SHA256 | `1e472bcfd49449e73ef76698c87f6583055aeb88dc41ca190a9c12c6791b5eea` |

本项目只做打包与安装编排，不分发任何修改过的二进制。`claude.exe` 为 Anthropic PBC
官方原版，未做任何改动，安装前后哈希一致。

Claude Code 本身的使用条款以 Anthropic 官方为准。
