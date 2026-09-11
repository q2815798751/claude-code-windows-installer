# Claude Code Windows 一键安装包 — 任务计划

> 目标：一个 `ClaudeCodeSetup.exe`，在全新 Windows 电脑上双击即完成 Claude Code 安装、
> 环境变量配置、桌面快捷启动，并在安装前做完整预检与用户确认。

---

## 1. 侦察结论（已完成的事实核查）

| 项目 | 结论 |
|---|---|
| 官方分发 | `https://github.com/anthropics/claude-code/releases` 提供原生 Windows 二进制 |
| 最新版本 | **v2.1.268**（2026-09-10 发布） |
| x64 载荷 | `claude-win32-x64.zip` — 99,598,997 字节 |
| arm64 载荷 | `claude-win32-arm64.zip` — 96,163,418 字节 |
| 官方校验 | `SHASUMS256.txt`（含 SHA256）+ `SHASUMS256.txt.sig` |
| x64 SHA256 | `50f44379a30cb8bac0d238654e140f5602c32ab6a2a98e80f649a8bcf35c41f9` |
| arm64 SHA256 | `c86e16b2cd12ee4539efc64a59d42d6ecdb4f770e64ace3206b508595848ced5` |
| `claude.ai/install.ps1` | **本地区域被屏蔽**（返回 "App unavailable in region"）→ 必须走 GitHub Release |

**本机构建环境**

| 工具 | 状态 |
|---|---|
| `csc.exe`（.NET Framework 4.8 内置） | ✅ 可用，**仅支持 C# 5**（无 `$""`、无 `?.`、无表达式体成员） |
| .NET Framework | ✅ 4.8（Release 533325） |
| PowerShell | ✅ 5.1 |
| IExpress | ✅ 备用打包方案 |
| NSIS / Inno Setup / 7-Zip | ❌ 未安装 → 不依赖 |
| Git for Windows | ✅ 2.45.1 @ `E:\Git`（bash: `E:\Git\bin\bash.exe`） |
| Windows Terminal | ✅ `wt.exe` 存在 |
| 系统 | Windows 10 Pro 22H2 build 19045，AMD64 |
| LongPathsEnabled | ❌ 0（未启用） |
| C: 可用空间 | 347 GB |

**关键技术决策**：不引入任何第三方打包工具，用 .NET Framework 内置 `csc.exe` 手写
WinForms 安装器，把官方载荷 zip 以「尾部追加 + trailer」方式合成单文件 exe。
这样构建链在本机与任何 Windows 10+ 机器上都零依赖可复现。

---

## 2. 产物结构

### 2.1 单文件安装包布局

```
ClaudeCodeSetup.exe
┌────────────────────────────────────────────┐
│ [0 .. N)   C# WinForms 引导程序 (stub)      │
│ [N .. M)   payload.zip  ← 官方原始 zip 字节 │
│ 结尾 16B   MAGIC "CLDCDSETUP-TRAIL"        │
│ 前 8B      payload 长度 (Int64 LE)          │
└────────────────────────────────────────────┘
```

运行时 stub 读取自身末尾 24 字节取得 MAGIC 与长度 → 定位载荷 → 解出到临时目录 → 解压。

### 2.2 安装后的磁盘布局

```
%LOCALAPPDATA%\Programs\ClaudeCode\
├── claude.exe                ← 官方原生二进制
├── (官方 zip 内其余文件)
├── Uninstall.exe             ← 卸载器
├── install.log               ← 安装日志（含所有预检结果）
└── manifest.json             ← 版本 / SHA256 / 安装时间

%USERPROFILE%\Desktop\Claude Code.lnk      ← 桌面快捷启动（终端）
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Claude Code.lnk
```

**采用「每用户安装」**（`HKCU` + `%LOCALAPPDATA%`）：
- 不触发 UAC，不需要管理员，不会因权限失败 —— 直接消除一大类安装失败原因
- PATH 写 `HKCU\Environment\Path`（用户级）
- 卸载信息写 `HKCU\...\CurrentVersion\Uninstall\ClaudeCode`

---

## 3. 安装器行为规格

### 阶段 0 — 预检（Preflight，全部只读，不改动任何东西）

| # | 检查项 | 判定 | 失败处理 |
|---|---|---|---|
| 1 | 操作系统 | Win10 1809+（build ≥ 17763）/ Win11 | 阻断，提示不支持 |
| 2 | CPU 架构 | x64 → 用内置载荷；arm64 → 走在线下载 | arm64 无网则阻断 |
| 3 | 可用磁盘 | 目标卷 ≥ 600 MB | 阻断 |
| 4 | 网络连通 | TCP+TLS 握手 `api.anthropic.com`、`github.com`，测 RTT 与吞吐 | **仅警告**（内置载荷可离线装）；arm64 下载路径则阻断 |
| 5 | Git for Windows | 存在且 `bash.exe` 可达 | 警告 + 提供下载入口；同时写入 `CLAUDE_CODE_GIT_BASH_PATH` |
| 6 | 已有安装 | 扫 PATH / npm 全局 / 旧版原生目录 | 提供 升级 / 修复 / 取消 |
| 7 | 进程占用 | 是否有 `claude.exe` 正在运行 | 提示关闭后再装 |
| 8 | 长路径支持 | `LongPathsEnabled` | 提示可选开启（需管理员，默认跳过） |
| 9 | Defender 实时防护 | 是否开启 | 仅提示，可能拖慢解压 |

### 阶段 1 — 确认（用户决策点）

单页摘要对话框，列出：
- 版本号 v2.1.268、来源 URL、SHA256
- 安装目录、预计占用空间、安装耗时估计
- 将要发生的全部变更：新增目录 / 写入 PATH / 桌面快捷方式 / 开始菜单 / 卸载项
- Git 状态与网络状态结论

按钮：**`[ 安装 ]`** / **`[ 取消 ]`** —— 满足「提醒用户是否决定安装」。

### 阶段 2 — 安装（带进度条与滚动日志）

1. 解包 → 目标目录（防御性：先写 `.tmp` 再原子改名）
2. 校验 `claude.exe` SHA256 == 官方 `SHASUMS256.txt` 值；不符即回滚
3. 冒烟测试 `claude.exe --version`
4. 写环境变量（HKCU）：`Path` 前置安装目录；`CLAUDE_CODE_GIT_BASH_PATH`
5. 建桌面快捷方式 → 优先 Windows Terminal（`wt.exe`），回退 `cmd.exe`；工作目录 `%USERPROFILE%`
6. 建开始菜单快捷方式
7. 落地 `Uninstall.exe` + `HKCU` 卸载注册项
8. 广播 `WM_SETTINGCHANGE` 让资源管理器刷新环境
9. 写 `install.log` / `manifest.json`

### 阶段 3 — 完整性检查与测试（安装后自动执行）

- 重读注册表确认 PATH 已写入且无重复项
- 用**新 PATH** 启动 `claude --version`，比对期望版本
- 校验安装目录文件数与总大小
- 校验快捷方式存在且目标可解析
- 打印逐项 ✅/❌ 结果；任一失败则给出回滚建议

### 卸载器 `Uninstall.exe`
移除安装目录、PATH 条目、桌面/开始菜单快捷方式、HKCU 卸载项。保留或询问是否保留 `%USERPROFILE%\.claude`（用户配置）。

---

## 4. 构建流程

```
build/
├── Installer.cs          # WinForms 安装器（C# 5）
├── Uninstaller.cs        # 卸载器（C# 5）
├── build.bat             # 一键构建：编译 stub → 合成带载荷的 exe → 校验
└── langtest.bat          # C#5 能力自检（已保留）
```

`build.bat` 步骤：
1. `csc` 编译 `Installer.cs` → `stub.exe`（`/target:winexe`）
2. 校验下载的 `payload/claude-win32-x64.zip` SHA256 == 官方值
3. `copy /b stub.exe + payload.zip + trailer` → `dist/ClaudeCodeSetup.exe`
4. 自检：读取成品末尾 trailer，确认偏移/长度/MAGIC 正确，再核对载荷段 SHA256

---

## 5. 测试计划

| 层级 | 内容 |
|---|---|
| 构建期 | 载荷 SHA256 比对官方 `SHASUMS256.txt`；成品 trailer 自检 |
| 逻辑 | 预检各分支可单独触发（`--selftest` 开关跑预检并打印、不安装） |
| 端到端 | 在**全新用户/沙箱环境**实跑安装 → 校验 PATH / 快捷方式 / `claude --version` → 再跑卸载 → 校验无残留 |
| 幂等 | 重复执行安装器应识别为「已安装」并走修复分支，不产生重复 PATH 条目 |

---

## 6. 交付物

1. `dist/ClaudeCodeSetup.exe` — 单文件一键安装包（内置官方 v2.1.268 载荷）
2. 私有 GitHub 仓库（含完整源码、构建脚本、中文 README）
3. 仓库 Release 中附带安装包，README 给出校验方式与 SHA256

---

## 7. 已确认事项

| 决策点 | 选择 | 结果 |
|---|---|---|
| 安装包形态 | **内置载荷单文件** | exe 约 95 MB，安装耗时实测 2.4–2.5 s，完全离线 |
| 架构覆盖 | **x64 内置 + ARM64 按需下载** | x64 走内置；ARM64 自动从官方 Release 拉取并校验 SHA256 |
| Git 缺失处理 | **检测 + 引导**，不代替用户安装第三方软件 | 预检告警 + 安装后给出 `winget install Git.Git` 与官网链接 |
| 启动方式 | 自动选择：优先 Windows Terminal，回退 cmd.exe | 两者都指向 `launch.cmd`，用全路径调用，不依赖环境变量刷新 |

## 8. 实测结果

| 项目 | 结果 |
|---|---|
| 官方载荷 SHA256 校验 | ✅ 与 `SHASUMS256.txt` 一致 |
| 解压产物 SHA256 | ✅ `1e472bcfd49449e7…` 与构建期记录一致 |
| 沙箱安装 → 卸载 | ✅ 退出码 0，目录清空 |
| 真实安装（含系统改动） | ✅ 2.47 s，9 项完整性验证全绿 |
| PATH 幂等性 | ✅ 二次安装后仍为 1 条，无重复 |
| 桌面快捷方式实际启动 | ✅ 拉起 OpenConsole → cmd → claude 进程 |
| `launch.cmd` 剥离 PATH | ✅ 仍能正常输出 `2.1.268 (Claude Code)` |
| 卸载还原 | ✅ 目录/快捷方式/注册表/PATH 全部还原至基线，`~/.claude` 完好 |
| GUI 全流程 | ✅ 预检页 → 安装进度页 → 验证报告页 |

**测试中发现并修复的缺陷**

1. `SslStream.AuthenticateAsClient(host)` 不带协议参数时只用 TLS 1.0，现代服务器
   一律拒绝 → 显式传协议集并逐个候选重试。
2. `SslStream` 握手无超时参数，对端不回应会挂到系统默认 ~21 s → 独立线程 + 看门狗。
3. 子步骤把自己的 0–100 百分比直接打到总进度条上，导致**进度条倒退** → 加 `Map()` 映射。
4. 进度回调每秒触发数十次并逐条写日志，刷出数百行 → 分离进度与日志两条通道。
5. `claude --version` 的整行输出被当成版本号写进注册表 `DisplayVersion` → 只取首个 token。
6. 注册表 `EstimatedSize` 硬编码 211 MB，与实际 216,443 KB 不符 → 用实际字节数换算。
7. **静默卸载默认删除 `~/.claude`**（用户的凭据/历史/项目数据），与 GUI 默认保留不一致
   → 静默路径也改为默认保留，删除需显式 `--delete-config`，并加目录特征护栏。
8. ARM64 分支只设了标志、从未实现下载逻辑 → 补齐下载 + 官方 SHA256 校验。

