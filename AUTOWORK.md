# 自动作业单 · claude-code-setup

> 本文件是自动作业的**唯一状态来源**。每次被唤醒时，先读本文件，找到「当前进度」里
> 第一个未完成的条目，做完、验证、提交、推送、勾选，然后停止。不要顺手做下一项。

## 硬性规则

1. **分支**：所有工作只在 `autowork/2026-10-16` 分支上。**永远不要碰 `main`**，
   不要 `force push`，不要改远端设置，不要删远端分支。
2. **一次一项**：每轮只推进一个条目。做完就停。
3. **每项必提交并推送**：`git add` → `git commit -m "[AW-<编号>] <做了什么>"` → `git push`。
   **不 push 等于没做。** 代理已配好（`.git/config` 的 `http.proxy` = Clash `127.0.0.1:7897`）。
   ⚠️ **别用 `git remote show origin` 判断网络好坏**——它不走代理配置会失败，
   但 `git push` / `git fetch` 是通的（已实测）。
4. **每项必验证**：
   - `AW-0` 建好之前：用一次性脚本验证，并在条目里写清怎么验的。
   - `AW-0` 之后：跑 **`build\unit.bat`**（自研 C# 单元测试，零依赖）。
   - 涉及安装器整体行为的条目，额外跑 **`build\test.bat`**——但**必须先做环境备份**，见规则 5。
   - 用例数**只增不减**，以「当前进度」区最新一条记录的数量为准。
   - **新增回归测试后，要临时撤掉修复验证它确实会变红**，再装回去。
     ⚠️ 做「撤掉修复」验证时**必须先确认回退本身生效了**（`git diff` 确认改动没了），
     否则得到的是假阳性——比不验证更糟。
5. **🔴 环境安全红线（最重要）**：`build\test.bat` 会**真的执行安装和卸载**，
   而卸载逻辑当前会**无条件删除用户级环境变量**（见 AW-1）。
   - **跑 `build\test.bat` 之前**，必须先执行环境备份：
     `reg export "HKCU\Environment" "%TEMP%\env-backup-<时间戳>.reg" /y`，
     记下备份路径；跑完**立刻比对** `HKCU\Environment` 是否被改动，
     被改动就**用备份恢复**（`reg import`）并在条目里如实记录「测试污染了环境，已恢复」。
   - `AW-1` 修完之前，**每轮跑 E2E 都要备份**；`AW-0` 要把这个备份/恢复做成一个
     可复用的脚本（如 `build\e2e-with-backup.bat`），不要每轮手敲。
   - **绝不运行 `dist\ClaudeCodeSetup.exe` 时不带 `--target-dir=<沙箱目录>`**——
     不许装到真实位置，不许做系统级改动（`--no-system` 是默认必须带上的）。
   - **绝不**手工执行卸载器、绝不手工删 `HKCU\Environment` 里的任何键。
   - 本机 `HKCU\Environment` 里有用户真实的
     `CLAUDE_CODE_DISABLE_UNKNOWN_MODEL_WINDOW_ENFORCEMENT`，**弄丢它算事故**。
   - 本机**没有**设置 `CLAUDE_CODE_GIT_BASH_PATH`（已确认），所以 AW-1 的修复
     在本机无法用「真实变量」验证，要用**注入测试**：临时设一个变量 → 跑沙箱卸载 →
     断言行为符合新契约 → 立刻还原。测试期间设的变量必须用完即清。
6. **同一项连续失败两次**就标记 `BLOCKED` 并写清原因，跳到下一项，不要死磕。
7. **不确定就跳过**：改产品行为、改默认值、改文档承诺这类需要用户决策的，记到「待确认」区，
   **不要擅自决定**。
8. **队列空了就停**：不要自己编条目凑数。
9. **本机环境**：Windows 10，.NET Framework 4.x，`csc.exe` 在
   `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（已确认存在）。
   项目是 **C# 5 + WinForms，零第三方依赖**——**不要引入任何第三方依赖或测试框架**，
   测试用系统 `csc.exe` 编译自研的 assert 式测试程序。
10. **仓库历史只有 2 个提交**，别指望 `git log` 考古。
11. **源码是 C# 5**（`csc.exe` 默认语言版本很老）：不要用 C# 6+ 语法
    （字符串插值 `$"..."`、`nameof`、表达式体成员、`?.`、自动属性初始化器）。
    写完先编译，编译不过就是语法越界了。

## 背景

这是一个 Windows 上 Claude Code 的**单文件一键安装器**：C# WinForms stub + 内嵌官方
`claude-win32-x64.zip`，产物 `dist\ClaudeCodeSetup.exe`（约 95 MB）。用系统 `csc.exe` 编译，
无第三方依赖。构建走 `build\build.bat`（6 步：查 csc → prep 校验 SHA256 并生成 BuildInfo →
编译 Uninstall.exe → 编译 stub.exe → pack → verify 回读 trailer 校验）。

**勘察结论（2026-10-16 人工审计，非猜测）**：
- 仓库**没有单元测试、没有 linter、没有 CI**；唯一的自动化验证是 `build\test.bat`，
  它会真的安装+卸载（沙箱目录，约 211 MB 写入）。
- 有 `--selftest` 只读自检（10 项预检），可用于不写盘的验证。
- 最严重的问题是**注释与实现背离**：卸载时声称「仅当变量指向本安装目录才移除」，
  实际无条件删除用户级 `CLAUDE_CODE_GIT_BASH_PATH`。
- 还有一处**假验证**：号称「用新 PATH 实际启动 claude」，实际用的是绝对路径，
  根本没经过 PATH 解析——这个验证项是无效的。

**顺序是刻意排的**：先建可验证性（AW-0），再修会破坏用户环境的问题（AW-1/AW-2），
然后正确性与文案（AW-3~AW-5），再健壮性（AW-6），最后文档与清理（AW-7~AW-12）。

## 当前进度

- [x] **AW-0 建验证基建（可验证性 + 环境安全）** —— 后面所有条目的前提，必须第一个做。
      两件事：
      (a) **C# 单元测试基建**：`tests\` 目录 + 一个 assert 式测试程序（纯 C# 5，零依赖），
      用系统 `csc.exe` 编译运行，入口 `build\unit.bat`，失败时**非零退出码**。
      要能测到 `src\Checks.cs` / `src\Common.cs` / `src\Installer.cs` 里的纯逻辑
      （例如 PATH 前置逻辑 `Util.PrependPath`、架构判定、磁盘空间判定、各种字符串/路径辅助）。
      编译时注意 `Program.cs` 和 `Uninstaller.cs` 各有一个 `Main`，
      用 `csc /main:<测试类>` 指定入口；GUI 程序集要 `/r:System.Windows.Forms.dll`。
      **首批用例 ≥ 15 个**，且**故意改坏一处逻辑能让对应用例变红**（验证测试有效）。
      (b) **E2E 环境备份包装脚本** `build\e2e-with-backup.bat`：跑 `build\test.bat` 前后
      自动 `reg export` / 比对 / 必要时 `reg import` 恢复 `HKCU\Environment`，
      并把「环境是否被改动」作为结论打印出来。这是规则 5 要求的强制护栏。
      顺手在 `README.md` 加一节说明 `build\unit.bat` 和 `build\e2e-with-backup.bat` 怎么用。
      完成标准：`build\unit.bat` 全绿；备份脚本能在一次真实 `build\test.bat` 运行中
      正确检出并报告环境变化。验证后把用例数记到本条目末尾。
      **结果（2026-10-17）**：完成。
      - (a) `tests\UnitTests.cs` + `build\unit.bat`：**23 个用例 / 47 个断言，全绿，退出码 0**。
        用系统 `csc.exe` 一次编译全部产品源码（`Program.cs`/`Uninstaller.cs`/测试类各有
        `Main`，用 `/main:ClaudeCodeSetup.UnitTests` 指定入口；GUI 程序集加
        `/r:System.Windows.Forms.dll`），因此 unit.bat 同时是一次全项目编译检查。
        覆盖 `Util.NormalizeDir/PathContains/PrependPath`、`InstallerCore.FirstToken`、
        `Util.NativeArch`（进程内注入环境变量，用完即还原）、`NetProbe.Score/Summary`、
        `Payload.Locate`（含合法/无魔术字/过短三种）、`SubStream` 读写寻址、
        `Util.Sha256File`、`Const.RequiredFreeBytes`。`src\BuildInfo.cs` 缺失时自动生成
        占位 stub，保证新克隆/CI 也能跑。
      - 测试有效性：故意把 `Util.PrependPath` 的 `dir + ";" + trimmed` 改成
        `trimmed + ";" + dir`，`git diff` 确认改动确实存在后重跑 → **2 个用例变红、退出码 1**；
        改回后 `git diff` 为空、再跑 **47/47 全绿**（先确认回退生效再取信）。
      - (b) `build\e2e-with-backup.bat`：`reg export` 备份 → 跑 `build\test.bat` →
        再导出并逐字节 `fc /b` 比对 → 变了就 `reg import` 恢复并复验 → 打印
        `UNCHANGED`/`CHANGED-RESTORED` 等结论；退出码透传 test.bat。
        两次真实 E2E 验证：① 不注入时 **E2E PASS、`HKCU\Environment: UNCHANGED`**；
        ② 注入哨兵 `CLAUDE_CODE_GIT_BASH_PATH` 后跑，卸载阶段把它删了（AW-1 的现象），
        脚本 **检出改动并 `reg import` 恢复、复验通过 → `CHANGED-RESTORED`**。
        收尾：手工删掉哨兵后与注入前的备份逐字节比对 **IDENTICAL**，
        `CLAUDE_CODE_DISABLE_UNKNOWN_MODEL_WINDOW_ENFORCEMENT` 完好。
      - README 新增「测试」一节 + 目录结构补 `tests/`、`unit.bat`、`e2e-with-backup.bat`。

- [x] **AW-1 修卸载误删用户环境变量（最高优先级，破坏用户环境）**
      问题：`src\Installer.cs:382` 注释写「仅当指向本安装目录时才移除」，
      但 `src\Installer.cs:385-392` 只判断 `!string.IsNullOrEmpty(cur)`，
      **随即无条件删除**用户级 `CLAUDE_CODE_GIT_BASH_PATH`。
      用户装 Claude Code 之前自己配好的 Git Bash 路径会被安装器的卸载顺手删掉，
      而且卸载后 Claude Code 就找不到 bash 了。
      修法：让实现兑现注释——只在该变量**由本安装器写入**（或值指向本次安装目录/本安装器
      管理的 Git Bash）时才删除。最稳妥的做法是在安装时把「我写过这个变量」记进
      `manifest.json`，卸载时据此判断；次优是比对路径是否位于安装目录下。
      **选哪种要写清理由**，并保证向后兼容（老 manifest 没有这个字段时不要乱删）。
      新增用例：变量指向别处 → 断言**不删**；变量指向安装目录 → 断言删；
      变量不存在 → 断言无操作不报错。
      **结果（2026-10-17）**：完成（commit e5fe923）。
      - **契约**：卸载时仅当该变量确由本安装器写入（安装时把 `gitBashEnvWritten=true` 与
        写入值 `gitBashPath` 记进 `manifest.json`）、**且当前值仍等于当时写入的值**时才删；
        另外若当前值落在本次安装目录内也删。其余（用户装前自配、安装后被改过、
        或老 manifest 无该字段）**一律保留**。
      - **选 manifest 记账法的理由**：真实 Git Bash 位于安装目录**之外**（如 `E:\Git\bin\bash.exe`），
        纯「路径是否在安装目录下」的比对几乎永远不命中，无法判断归属；manifest 记账精确，
        且向后兼容（老 manifest 没有 `gitBashEnvWritten` 字段 → `ReadGitBashManifest` 返回 false
        → 不据此删除）。路径规则只作兜底。
      - **实现**（`src\Installer.cs`，C# 5）：安装路径加 `gitBashEnvWritten` 标记；`WriteManifest`
        新增 `gitBashEnvWritten` 字段、`gitBashPath` 仅在写入时记录；卸载路径用
        `ReadGitBashManifest` 读清单 + 纯函数 `ShouldRemoveGitBashEnv`/`PathIsUnder` 判定，
        删/留都记日志。清单在删除安装目录**之前**读取。
      - **单测**：新增 6 个用例 / 21 个断言（`ShouldRemoveGitBashEnv` 三态 + manifest 归属三态、
        `PathIsUnder`、`ReadGitBashManifest` 读写含老 manifest/缺失文件）。
        `build\unit.bat` **29 用例 / 68 断言全绿，退出码 0**（AW-0 为 23/47）。
      - **测试有效性**：把 `ShouldRemoveGitBashEnv` 临时改回旧的无条件删除逻辑，
        `git diff` 确认回退**确实生效**后重跑 → **5 个断言变红、退出码 1**；改回后再
        `git diff` 确认该改动已消失、重跑 **68/68 全绿**（先确认回退生效再取信）。
      - **E2E 注入验证（新契约）**：`reg export` 备份 `HKCU\Environment` → 注入哨兵
        `CLAUDE_CODE_GIT_BASH_PATH=C:\aw1-sentinel\bin\bash.exe` → 重编译 `dist`（含修复）→
        `build\e2e-with-backup.bat` 跑沙箱装/卸：**E2E PASS，卸载后哨兵仍在**（新契约：不删），
        脚本报 **`HKCU\Environment: UNCHANGED`**；沙箱 manifest 为 `gitBashEnvWritten:false`（正确）。
        手工删哨兵后与**注入前**备份逐字节 `cmp` **IDENTICAL**，
        `CLAUDE_CODE_DISABLE_UNKNOWN_MODEL_WINDOW_ENFORCEMENT` 完好。
      - **已知局限（未处理，见「待确认」）**：若用户安装前已自配该变量、安装器把它覆盖为探测到的
        路径，则 manifest 记为「本安装器写入」，卸载按契约删除 → 用户原值丢失。是否要
        「记录并在卸载时还原用户原值」属产品决策。

- [x] **AW-2 修「用新 PATH 启动」的假验证（正确性 + 验证有效性）**
      问题：`src\Installer.cs:283-296` 第 286 行把安装目录并入 PATH，
      第 288 行却用 `RunCapture(exe, …)`，而 `exe`（第 245 行）是 `dir\claude.exe` 的**绝对路径**。
      这个「验证」根本不经过 PATH 解析，`README.md:82` 与 `docs\PLAN.md:120`
      宣称的「用新 PATH 实际启动 `claude --version`」**从未被真正验证**。
      修法：改为通过 PATH 调用（传 `claude` 而非绝对路径），
      并确保用的是**新 PATH**（临时进程环境，不要污染当前进程/系统）。
      若改为真走 PATH 后发现失败，说明 PATH 生效链路有真问题——**那是真 bug，不要为了让它变绿而退回绝对路径**，
      如实记录并判断是否该标 BLOCKED。
      新增用例：断言调用参数是裸命令名而非绝对路径（可用可注入的 runner 打桩）。
      **结果（2026-10-17）**：完成（commit 916c4cf）。
      - **契约**：安装后验证第 3 步必须**真走 PATH**——传裸命令名 `claude`，让它经 PATH 解析到
        安装目录的 claude.exe；新 PATH 只注入**临时子进程环境**，不碰当前进程/系统。
        旧实现传 `dir\claude.exe` 绝对路径，根本没经过 PATH，是假验证。
      - **实测关键发现（决定了实现方式）**：`Process.Start("claude", …)`（`UseShellExecute=false`）
        配 `psi.EnvironmentVariables["PATH"]=新PATH` **解析不到**——CreateProcess 解析裸命令名用的是
        **调用进程**的 PATH，对子进程环境块里的 PATH 覆盖无效（本机探针实测：直接传裸名报
        「系统找不到指定的文件」；只有改当前进程 PATH 才行）。故改为借 `cmd.exe /c claude --version`：
        cmd 按**自身环境**里的 PATH 解析，既能真走 PATH，又只需注入子进程环境、不污染当前进程。
      - **第二个陷阱**：工作目录不能是安装目录，否则命令会被「当前目录」直接命中而**再次绕过 PATH**
        （cmd 默认搜当前目录；本机因设了 `NoDefaultCurrentDirectoryInExePath=1` 才恰好没暴露）。
        故验证时改用**中性工作目录**（用户主目录），确保命令只能靠 PATH 解析到。
      - **实现**（`src\Installer.cs`，C# 5）：新增可注入委托 `PathRunner`；`VerifyLaunchViaPath(dir, r, runner)`
        传裸名 `"claude"` + 新 PATH（`Util.PrependPath`）；`RunViaPath` 用 `cmd.exe /c` 真正按 PATH 执行；
        `RunCapture` 新增 `pathOverride` 重载（只写 `psi.EnvironmentVariables["PATH"]`，绝不动当前进程）。
        删掉旧的「临时改当前进程 PATH + 绝对路径」块。
      - **单测**：新增 2 个用例 / 12 个断言（裸命令名、非绝对路径、新 PATH 注入、工作目录中性、
        当前进程 PATH 不变、失败记录）。`build\unit.bat` **31 用例 / 80 断言全绿，退出码 0**（AW-1 为 29/68）。
      - **测试有效性**：把 `runner("claude", …)` 临时改回绝对路径 `Path.Combine(dir, "claude.exe")`，
        `git diff` 确认改动**确实存在**后重跑 → **2 个断言变红、退出码 1**；改回后 `git diff` 中该行消失、
        重跑 **80/80 全绿**（先确认回退生效再取信）。
      - **E2E（含真安装）**：重编译 `dist` → `build\e2e-with-backup.bat` 跑沙箱装/卸：**E2E PASS**，
        验证第 3 步打印 `[OK] 启动测试 claude --version 2.1.268 (Claude Code)`——**真走 PATH 的启动链路工作正常**，
        **未发现真 bug，无需 BLOCKED**。`HKCU\Environment: UNCHANGED`（逐字节比对）。
        备份 .reg（含明文凭据）跑完**已立即删除**，`%TEMP%` 无残留；
        `ANTHROPIC_AUTH_TOKEN` 与 `CLAUDE_CODE_DISABLE_UNKNOWN_MODEL_WINDOW_ENFORCEMENT` 均在。
      - **文档**：`README.md:82`、`docs\PLAN.md:120` 宣称的「用新 PATH 实际启动 `claude --version`」
        现在**名副其实**，无需改文档。

- [x] **AW-16 修 `unit.bat` 的 BuildInfo stub 路径 bug（AW-0 复查实测，中等）**
      > 编号后补，但**优先级高于 AW-2**——它挡着「在干净克隆里验证」这条所有人都会走的路。

      AW-0 的对抗性复查**实测**出一个真 bug：`build\unit.bat` 第 49-66 行的
      `if not exist (...)` 块里，`set "BI=%WORK%\BuildInfo.stub.cs"` 之后仍用 `%BI%` 做重定向，
      而**块内 `%BI%` 在整块解析时就被展开成旧值**（`src\BuildInfo.cs`）。
      在无 `src\BuildInfo.cs` 的全新克隆里实测后果：
      1. stub 被**错误地写进 `src\BuildInfo.cs`**（污染源码目录，553 字节）；
      2. 到编译那一步 `%BI%` 才变成新值 → 文件不存在 → `error CS2001`，**退出码 1**；
      3. 第二次运行因为 `src\BuildInfo.cs` 已经存在，反而「自愈」了。
      **本机一直能跑，只是因为 `src\BuildInfo.cs` 恰好存在**——这条分支在本机从未被走到过。
      所以 README/AUTOWORK 里「`src\BuildInfo.cs` 缺失时自动生成占位 stub，保证新克隆/CI
      也能跑」**是假的**，而 AW-14 想加的 CI 若只跑 `unit.bat` 会**首跑即红**。
      修法：块内改用 `!BI!`（`setlocal EnableDelayedExpansion` 已经开了），或把赋值移出 `if` 块。
      ⚠️ **改完必须在一个真正的干净克隆里验证**（`git clone` 到临时目录再跑）——
      在原仓库跑测不出这个 bug。
      验证方式：干净克隆里 `build\unit.bat` 退出码 0，**且没有生成 `src\BuildInfo.cs`**
      （`git status` 干净）。
      顺手（同属 AW-0 遗留的文档不实）：README 的「测试」节仍写着「卸载路径当前会无条件删除
      用户级环境变量（见 AW-1）」，而 **AW-1 已经修掉了**——改成如实描述。
      ⚠️ **AW-2 复查补充的两点（同属这一项的范围）**：
      1. **新增的单测不是判别性的**。AW-2 新增的 2 个断言用 **stub `PathRunner`**，
         只断言「调用方传了裸名 `claude`」「注入了新 PATH」「工作目录中性」——
         **stub 不碰 PATH，所以它们证明不了 PATH 被解析**；而 E2E 也不判别
         （真实安装时 PATH 必然含安装目录，真假验证都会 PASS）。
         复查是靠**自写 harness 直接调生产代码 `RunViaPath`** 做判别实验才确认修复为真的
         （PATH 含安装目录 → 成功；不含 → 失败；绝对路径 + PATH 不含 → 仍成功，
         反证旧实现确实绕过 PATH）。**那个 harness 应该移植成仓库里的正式用例**：
         临时目录放假 `claude.cmd`，分别用「含 / 不含该目录的 PATH」调 `RunViaPath`，
         断言一成功一失败。**没有这条，将来谁把这个验证改回假验证，现有测试抓不到。**
      2. **新测试在干净克隆下会假红 1 条**：`Test_VerifyLaunchViaPath_ReportsFailure`
         依赖 `BuildInfo.ReleaseVersion != "0.0.0"`，而干净克隆里 `unit.bat` 生成的 stub
         恰好是 `0.0.0` → 该断言假红。这与本项的 stub 路径 bug **同源**，
         修 stub 时一并把这条测试改成不依赖具体版本值。
      **结果（2026-10-17）**：完成（commit 7abf9ad）。
      - **修法**（`build\unit.bat`）：`if not exist` 块内所有 `%BI%` 改为 `!BI!`
        （`setlocal EnableDelayedExpansion` 已开），并加注释说明为何必须延迟展开。
      - **干净克隆复现 + 验证**（本项的关键，`git clone` 到 `%TEMP%` 下的临时目录）：
        修复前 clone 里跑 `build\unit.bat` → stub 被写进 `src\BuildInfo.cs`（553 字节）、
        编译报 `error CS2001 ... build\work\BuildInfo.stub.cs`、**退出码 1**（bug 复现）；
        修复后（从 commit 7abf9ad 重新 clone）→ **退出码 0、32 用例 / 83 断言全绿、
        `src\BuildInfo.cs` 未生成**（stub 正确落在 `build\work\BuildInfo.stub.cs`）、
        `git status` 干净。
        ⚠️ 注意：`src\BuildInfo.cs` 在 `.gitignore` 里，**光看 `git status` 干净不足以
        证明没被污染**——必须显式 `ls src\BuildInfo.cs` 确认文件不存在。
      - **判别性用例移植**（`tests\UnitTests.cs`，新增 `Test_RunViaPath_ResolvesThroughPath`，
        1 用例 / 3 断言）：临时目录里放假 `claude.cmd`（只以 `.cmd` 形式存在，只能经 PATH
        + PATHEXT 解析到），分别用「PATH 含该目录」和「PATH 不含该目录」调**生产代码**
        `InstallerCore.RunViaPath`，断言前者退出码 0 且输出含假命令标记、后者非 0。
        中性工作目录（不含 `claude.cmd`），排除「当前目录命中」的干扰。
      - **测试有效性（撤掉修复验证变红）**：把 `RunViaPath` 里传给 `RunCapture` 的
        `pathOverride` 临时改成 `null`（即不再按注入的 PATH 解析），`git diff` 确认改动
        **确实存在**后重跑 → **2 个断言变红、退出码 1**（新用例）；还原后 `git diff`
        该行为空、重跑 **83/83 全绿**（先确认回退生效再取信）。
      - **假红修复**：`Test_VerifyLaunchViaPath_ReportsFailure` 里第二个 runner 的
        输出从 `"0.0.0-not-the-expected-version"` 改成 `"unexpected-output-not-a-version"`
        （不含任何版本号），使其不再依赖 `BuildInfo.ReleaseVersion` 的具体值；
        干净克隆（stub 版本 = `0.0.0`）下该用例已绿。
      - **用例数**：`build\unit.bat` **32 用例 / 83 断言全绿，退出码 0**（AW-2 为 31/80）。
      - **文档**：`README.md` 的「测试」节删掉已被 AW-1 修掉的不实描述
        （「卸载路径当前会无条件删除用户级环境变量」），改为如实描述「会写入用户级
        环境变量，护栏仍值得保留；无条件删除问题已在 AW-1 修复」。

- [x] **AW-17 修 E2E 护栏把明文凭据留在 `%TEMP%`（AW-0 复查实测，安全）**
      > 编号后补，优先级高于 AW-2（涉及真实凭据；用户已于 2026-10-17 拍板处理方式）。

      AW-0 的对抗性复查**实测**出：`build\e2e-with-backup.bat` 按设计**保留**
      `HKCU\Environment` 的完整备份到 `%TEMP%` 供人工恢复，而该注册表键里有用户**真实的
      `ANTHROPIC_AUTH_TOKEN`（明文）**。跑一次留一份，几次下来堆一堆。
      （2026-10-17 已按用户决定清掉当时的 4 份明文副本，基线改成只留「键名 + 值 sha256」清单。）
      **用户已拍板的修法**：
      1. **默认「用完即删」**：备份只在运行期间存在，跑完就删。
      2. 只有**显式传 `--keep-backup`** 才保留；保留时必须**打印明文凭据警告**，
         并列出该文件里含哪些敏感键名。
      3. **比对逻辑不要依赖保留的明文**——比对的是「有没有变」，
         用「键名 + 值 sha256」的清单就够，盘上不留明文。
      4. 顺手把 README 里「非交互环境不加 `--yes` 就只打印计划、不落盘」改成如实描述
         （实测无论有没有 `--yes`，快照都会先写盘）。
      验证方式：跑一次护栏 → 断言 `%TEMP%` 下**没有**残留 `.reg`；
      加 `--keep-backup` → 断言保留了**且**打印了警告；
      比对结论仍正确（注入哨兵 → `CHANGED-RESTORED`；不注入 → `UNCHANGED`）。
      ⚠️ 修的时候**别把护栏的核心保证改坏**——复查已确认它是真安全门：
      真 `reg export`、真逐字节 `fc /b` 比对、真 `reg import` 恢复复验，
      而且**备份失败时会拒绝运行 E2E**（这点比 dsh 那边的护栏强）。这条必须保持。
      **结果（2026-10-17）**：完成（commit b71790c）。
      - **契约**：`build\e2e-with-backup.bat` 的备份**默认只在运行期间存在，跑完立即删除**；
        只有显式传 `--keep-backup` 才保留，保留时打印**明文凭据警告**并列出该文件里含哪些
        **敏感值名**。比对结论不再依赖保留的明文——每次 `reg export` 之后另存一份
        **「值名 + 值 sha256」清单**，两份清单也会一起比对。
      - **实现**：
        - `build\e2e-with-backup.bat`：新增 `--keep-backup` 参数解析（用 `goto` 循环逐行 `set`，
          在 `DisableDelayedExpansion` 下也能正确累积转发参数，且不把该开关转给 `test.bat`）；
          cleanup 默认删 `.reg` 与清单，`--keep-backup` 走警告分支（`findstr` 在清单里筛敏感值名）。
        - 新增 `build\env-hash.ps1`（ASCII-only，PowerShell 5.1）：把 `reg export` 产物按
          **UTF-16LE** 读入（普通文本工具直搜会假阴性），逐值写 `<值名>\t<sha256>` 清单，盘上不含明文。
      - **护栏核心保证逐条保持**：真 `reg export`、真逐字节 `fc /b` 比对（`.reg` 与清单各比一次）、
        真 `reg import` 恢复 + 重新导出复验、**备份失败即拒绝运行 E2E**（`exit /b 1`）、
        退出码透传 `test.bat`。清单比对失败是非致命的（`fc /b` 仍是权威判定）。
      - **验证**：
        - 真实 E2E（默认，无开关）：**E2E 跑完 → `HKCU\Environment: UNCHANGED`、备份已删、
          `%TEMP%` 无 `env-backup*`/`env-after*` 残留**。（该次 E2E 自身报 FAIL：卸载后
          `claude.exe` 仍在——是 `test.bat` 的**既有竞态**：卸载器目录被占用时走
          `ScheduleDelete` 延迟删除，而 `test.bat` 同步检查；目录稍后确实消失。与 AW-17 无关。）
        - `--keep-backup`：备份**保留**、打印明文凭据警告、并列出敏感值名
          `ANTHROPIC_AUTH_TOKEN`（只列名 + hash，无明文）；测完立即手工删除该文件。
        - **撤掉修复确认变红（定点破坏）**：把 cleanup 里删备份的两行 `del` 改成一句
          `rem`（模拟修复前的泄漏），`git diff` 确认改动存在后跑护栏 → `%TEMP%` **出现**
          `env-backup-*.reg` → 「无残留」断言**变红**；还原后 `git diff` 确认该 `rem` 已消失、
          再跑 → 无残留 → **绿**。
        - **CHANGED-RESTORED**：向**真实** `HKCU\Environment` 注入哨兵被**权限系统拒绝**
          （正确的安全门，未绕过）。改用**等价 harness**：把护栏复制到临时目录、仅把
          目标键换成一次性 scratch 键 `HKCU\Software\AW17Scratch`、`TEST` 换成一个删除
          `Sentinel` 的 stub（**比对/恢复逻辑与仓库版本逐字节相同**，`git diff` 佐证），
          跑出 **`CHANGED-RESTORED`**、`Sentinel` 被 `reg import` 还原；scratch 键测完已删。
        - 单测 `build\unit.bat`：**32 用例 / 83 断言全绿，退出码 0**（未改 C#，与 AW-16 同）。
        - **收尾核对**：`HKCU\Environment` 完好——9 个键，`ANTHROPIC_AUTH_TOKEN` 与
          `CLAUDE_CODE_DISABLE_UNKNOWN_MODEL_WINDOW_ENFORCEMENT` 都在，无哨兵残留；
          `%TEMP%` 无 `env-backup*`/`env-after*`。
      - **文档**：`README.md` 的「测试」节补 `--keep-backup`、凭据安全说明与「清单不依赖明文」；
        目录结构加 `build\env-hash.ps1`。
      - ⚠️ **AW-17 第 4 点（顺手项）未适用**：全仓（含 `git log --all`）grep `--yes`
        **只命中 AUTOWORK.md 自身**，README 里**没有**「非交互环境不加 `--yes` 就只打印计划、
        不落盘」这段文案，本仓库也没有 `--yes`/dry-run 概念——**无可改**，如实记录
        （疑似来自其他项目护栏文档的串味）。

- [x] **AW-18 查清真实 E2E 的「卸载后 claude.exe 仍在」假失败（AW-17 实测发现，测试基建可信度）**

      AW-17 的 worker 跑**真实 E2E** 时发现 `build\test.bat` **自身报 FAIL**：
      Phase 2 卸载后检查 `claude.exe` 仍在，于是 `E2E RESULT: FAIL`。
      它判断这是 test.bat 的**既有竞态**（卸载器的延迟删除 vs 同步检查）、**与 AW-17 无关**——
      这个判断合理，但它**没有查到底**，所以本项要查清并修掉。
      **为什么要修**：一个会假红的 E2E 会让人不再相信 E2E，
      之后真出问题也会被当成「又抽风了」。这是测试基建可信度问题，比它看起来重要。
      要做的事：
      1. **先复现并定位**：跑一次真实 E2E，确认 FAIL 的具体成因。注意 `test.bat` 头部注释写着
         「被测 exe 在沙箱之外，所以卸载器的延迟自删不参与，应当期望目录消失」——
         如果实际不成立，说明**注释或实现有一方错了**，查清是哪一方。
      2. **修法（写清理由）**：要么让检查等延迟删除完成（带超时，超时才报 FAIL），
         要么让卸载器在同步路径上完成删除（如果那才是契约）。
         **不要用「把检查删掉」来让它变绿。**
      3. **顺手修正 `test.bat` 头部那条注释**，让它与实现一致。
      验证方式：连续跑 E2E 若干次确认**稳定 PASS**（不是碰巧）；
      并且**故意制造一次真失败**（例如让卸载器少删一个文件）确认它**仍能报 FAIL**——
      即修完不会退化成「永远绿」。这两条缺一不可。
      **结果（2026-10-17）**：完成（commit dea8b61）。
      - **真实成因（复现 + 定位）**：`build\test.bat` Phase 2 卸载后**同步**检查
        `if exist "%SANDBOX%\claude.exe"`，而卸载器的目录删除是**异步**的——
        `InstallerCore.Uninstall` 里 `Directory.Delete(dir, true)` 一旦因**任何**瞬时句柄占用
        （杀软扫描刚写入的 221 MB `claude.exe`、句柄尚未释放等）抛异常，就回退到
        `ScheduleDelete`：一个脱离的 `cmd`，`ping -n 4`（约 3s）后 `rd /s /q`。
        检查跑在删除之前，于是**假 FAIL**；目录约 4s 后确实消失。
        用「持有 `claude.exe` 句柄（`FileShare.None`）→ 卸载」**确定性复现**：
        卸载返回瞬间 `claude.exe` 仍在（旧检查 FAIL），释放句柄后延迟删除完成、目录消失。
      - **注释与实现哪一方错**：**注释错**。旧注释写「被测 exe 在沙箱之外，所以卸载器的
        延迟自删不参与，应当期望目录消失」——它把「延迟删除」等同于「自删」。
        自删确实不参与（被测 exe 是 `dist\ClaudeCodeSetup.exe`，在沙箱外），
        但**延迟删除并不只由自锁触发**：任何瞬时锁都会走 `ScheduleDelete`。
        实现（异步回退 + 延迟删除）是**有意的、正确的**；注释的推理不成立。
      - **修法及理由**：选「让检查等延迟删除完成（带超时）」，而非「让卸载器同步删除」。
        理由：卸载器**保留**延迟删除回退是正确设计（真实场景里 `Uninstall.exe` 从沙箱内
        自删时根本不可能同步删掉自己），所以异步才是契约；E2E 必须容忍这个已文档化的延迟。
        实现：Phase 2 的 claude.exe 检查改为**带超时的轮询**——
        `for /L %%I in (1,1,30)` 每轮 `ping -n 2`（约 1s），文件消失即 `goto` 跳出，
        最多等约 30s；**超时仍报 FAIL**（没有删掉检查，也没有无条件放行）。
        正常情况第一轮就跳出（不拖慢），只在真延迟时等待。
      - **注释**：按「注释错」的结论重写，与实现一致（说明删除是异步的、以及为何轮询）。
      - **验证（缺一不可，全部完成）**：
        1. **稳定 PASS**：干净 `dist` 下连续跑 `build\e2e-with-backup.bat` **6 次全部 PASS、
           exit 0、`HKCU\Environment: UNCHANGED`**（改前另跑过 15 次 PASS 作基线）。
        2. **仍能报 FAIL（不退化成永远绿）**：临时把 `Uninstall` 改成「删掉除 `claude.exe`
           以外的所有文件、且不安排延迟删除」（`git diff` 确认改动存在），重编译 → 跑真实
           `build\e2e-with-backup.bat` → **`FAIL claude.exe still present after uninstall
           (waited ~30s...)`、`E2E RESULT: FAIL`、exit 1**；随后 `git checkout -- src\Installer.cs`
           回退、`git diff` 确认改动消失后重编译。
        3. **定点复现「旧 FAIL / 新 PASS」**：把 HEAD 版与修复版 `test.bat` 各复制一份，
           仅在 Phase 2 卸载前后注入「持有 `claude.exe` 句柄 / 卸载后释放」两行（其余逐字节相同），
           跑同一锁场景：**旧版 `E2E RESULT: FAIL`（exit 1）**、**新版 `E2E RESULT: PASS`（exit 0，
           轮询等到延迟删除完成）**。harness 副本与临时脚本跑完已删。
      - **单测**：`build\unit.bat` **32 用例 / 83 断言全绿，退出码 0**（未改 C#，与 AW-16 同）。
      - **环境安全**：全部实验用 `--no-system`（无 PATH/env 写入）；每次真实 E2E 都经
        `build\e2e-with-backup.bat` 备份/比对；收尾 `HKCU\Environment` 与实验前备份
        **逐字节 IDENTICAL**（9 个键，`ANTHROPIC_AUTH_TOKEN` 与
        `CLAUDE_CODE_DISABLE_UNKNOWN_MODEL_WINDOW_ENFORCEMENT` 均在）；
        手工备份的明文 `.reg` 用完**立即删除**，`%TEMP%` 无 `env-backup*`/`env-after*`/
        `cc-uninstall-*` 残留。

- [x] **AW-3 修预检文案与实现相反（正确性）**
      ⚠️ **前置要求（AW-0 复查点名）**：`src\Checks.cs` **当前零单测覆盖**——
      而这一项和 AW-4 要改的正是它。README/AUTOWORK 里「覆盖 Checks.cs 纯逻辑」的说法
      **不实**（被测的 `Payload`/`SubStream`/`NetProbe`/`Const` 全在 `Common.cs` 里）。
      所以**先给 `Checks.cs` 里要动的那个判定函数补用例**（确认描述旧行为、是绿的），再改。
      后续任何动 `Checks.cs` 的条目同理，**别高估现有基建的覆盖率**——
      那 23 个用例全是小工具函数，只是「机制」不是「覆盖」。
      问题：`src\Checks.cs:425-426` 提示「继续安装将把原生版本装到 … 并置入 PATH **末尾**」，
      但实现是 `AddToUserPath` → `Util.PrependPath`（`src\Installer.cs:668`、
      `src\Common.cs:303-309`）**前置**。`README.md:105` 和 `docs\PLAN.md:110` 都写「前置」，
      所以是 `Checks.cs:425` 错了。
      修法：改文案为「前置」。注意这条是**用户可见的提示文字**，
      改完要在条目里写明改了哪几个字。
      新增用例：断言提示文案包含「前置」且不含「末尾」（字符串级测试，防回归）。
      **结果（2026-10-17）**：完成（commit d2663ec）。
      - **改的字**：`src\Checks.cs` 里「已有安装」预检的提示文案，仅把
        「并置入 PATH **末尾**」改成「并置入 PATH **前置**」——即 `末尾` → `前置`
        （2 个汉字），其余文字未动。核对过实现：`AddToUserPath`（`src\Installer.cs:718`）
        → `Util.PrependPath`（`src\Common.cs:303`）返回 `dir + ";" + trimmed`，确是**前置**；
        `README.md:105` 与 `docs\PLAN.md:110` 也都写「前置」，所以错在 `Checks.cs`。
      - **补 Checks.cs 用例（前置要求）**：`CheckExisting` 要探测文件系统/PATH，
        单测里无法确定性驱动，故把用户可见文案抽成**纯函数** `Preflight.ExistingAdvice(dir)`
        （`CheckExisting` 改为调它，行为不变），让措辞可被单测直接断言。
        新增 1 个用例 / 3 个断言 `CheckExisting_AdviceSaysPrepend`（含「前置」、不含「末尾」、
        含安装目录）。中文断言用 `前置` / `末尾` 转义写，保持测试文件 ASCII-only。
      - **先绿后改**：先让用例断言**旧行为**（含「末尾」、不含「前置」）跑一遍 → **33 用例 /
        86 断言全绿**，证明用例确实打到了该函数、且如实描述现状；**再**改文案并把用例翻成
        新断言（含「前置」、不含「末尾」）→ 全绿。
      - **撤掉修复确认变红（定点破坏）**：只把 `ExistingAdvice` 里的 `前置` 改回 `末尾`
        （不碰其他任何改动），`grep` 确认工作树那一行确实是 `末尾`（回退生效）后重跑 →
        **2 个断言变红、退出码 1**；改回 `前置` 后 `grep` 确认已恢复、重跑 **86/86 全绿**
        （先确认回退生效再取信）。
      - **用例数**：`build\unit.bat` **33 用例 / 86 断言全绿，退出码 0**（AW-18 为 32/83）。
      - **红线**：本项只跑 `unit.bat`，未跑 E2E，未触碰 `HKCU\Environment`；未引入任何第三方依赖；
        源码保持 C# 5（纯函数 + 字符串拼接，无新语法）。
      - **无卡点**。

- [x] **AW-4 修 32 位 Windows 被误判为 x64（正确性）**
      问题：`src\Checks.cs:228` 把 `"AMD64"` 与 `"X86"` 合并成同一分支，
      返回 Pass、`UseBundled=true`，文案「x64 — 使用内置载荷」。
      32 位 Win10（`PROCESSOR_ARCHITECTURE=X86`）会通过 OS 检查（build ≥ 17763）
      与架构检查，然后**装上一个 64 位 claude.exe**，直到冒烟测试才失败。
      修法：把 `X86` 从 x64 分支拆出来，返回 Fail 或 Warn（32 位不受支持）。
      ⚠️ **产品决策**：「明确拒绝」还是「警告后仍允许继续」属于用户决策。
      默认按**拒绝（Fail）**处理并在「待确认」区登记；如果你判断原设计有意允许，
      就记到待确认、不要擅自改。
      新增用例：`X86` → 断言不再返回 Pass；`AMD64` → 断言仍 Pass。
      **结果（2026-10-17）**：完成（commit 0f753b9）。
      - **契约**：32 位 Windows（`Util.NativeArch()` = `X86`）**明确拒绝安装**——
        `ArchVerdict("X86")` 返回 `Fail`（阻断性失败，`HasBlockingFailure=true`），
        GUI 里安装按钮被禁用（`MainForm.OnPreflightDone`）、静默安装返回 2
        （`Program.RunSilentInstall` 见 `pre.HasBlockingFailure` 即中止）。内置载荷是
        64 位 `claude.exe`，32 位装上必然到冒烟测试才失败，故拒绝比「警告后继续」更诚实。
      - **可测性（AW-3 同款）**：`CheckArch` 依赖 `Util.NativeArch()`（读进程环境变量），
        单测里无法确定性地驱动到每个分支——原实现**不可注入**。故把判定逻辑抽成
        **可注入架构字符串的纯函数** `Preflight.ArchVerdict(string a, out bool useBundled)`
        （返回 `CheckItem`），`CheckArch` 只负责取架构、回填 `res.Arch`/`res.UseBundled`，
        行为不变。`X86` 从 `AMD64 || X86` 分支**拆成独立分支**。
      - **实现**（`src\Checks.cs`，C# 5）：`ArchVerdict` 四分支——`AMD64`→Pass(useBundled=true)；
        `X86`→**Fail**(useBundled=true)；`ARM64`→Info(useBundled=false)；其他/未知→Warn(useBundled=true)。
        `X86` 的 `useBundled` 保持 `true` 只是沿用旧值：该状态是阻断性失败、安装不会继续；
        取 `false` 反而会让网络预检误报「ARM64 需从 GitHub 下载」，故保持 `true`（代码有注释）。
        `X86` 的 Detail=「32 位 Windows (X86) — 不受支持」，Advice 说明只提供 64 位版本。
      - **单测**：新增 1 个用例 / 7 个断言 `ArchVerdict_X86Rejected`——`X86` 断言
        `State != Pass`、`State == Fail`、有 Advice；`AMD64` 断言 `Pass` 且 `useBundled==true`；
        另断言 `ARM64`→`Info`+非内置、未知架构→`Warn`+按 x64 处理（防回归）。
        `build\unit.bat` **34 用例 / 93 断言全绿，退出码 0**（AW-3 为 33/86）。
      - **撤掉修复确认变红（定点破坏）**：只把 `ArchVerdict` 里 `X86` 分支的
        `c.State = CheckState.Fail` 改回 `CheckState.Pass`（复现原 bug，不碰其他任何改动），
        `grep` 确认工作树那一行确实是 `Pass`（回退生效）后重跑 → **2 个断言变红、退出码 1**；
        改回 `Fail` 后 `grep` 确认已恢复、重跑 **93/93 全绿**（先确认回退生效再取信）。
      - **待确认**：本项按「拒绝（Fail）」实现，已在「待确认」区登记（原条目已存在，未新增）：
        若安装器有意支持 32 位机器，应改为「警告 + 允许继续」。
      - **红线**：本项只跑 `unit.bat`，**未跑 E2E、未触碰 `HKCU\Environment`**；
        未引入任何第三方依赖；源码保持 C# 5（纯函数 + `out` 参数，无新语法）；
        显式 `ls` 确认 `src\BuildInfo.cs` 仍在（未被 stub 污染）。**无卡点**。

- [x] **AW-5 修 `--keep-config` 无效开关（正确性，测试脚本的自欺）**
      问题：`build\test.bat:68` 传 `--keep-config`，但 `src\Program.cs:195` 只识别
      `--delete-config`（保留才是默认值），`--keep-config` 被**静默忽略**，
      测试脚本给人「显式保留了配置」的错觉。
      修法：二选一——(a) 让 `Program.cs` 显式支持 `--keep-config`（幂等、明确），
      或 (b) 从 `test.bat` 删掉它。**推荐 (a)**：显式优于隐含，且能让测试脚本表达真实意图。
      顺手：`Program.cs` 遇到**未知开关**时应该给出警告而不是静默忽略（如果现在就是静默忽略），
      这样以后类似的笔误不会再被吞掉。这条如果改动面大，单独记为后续条目，别在这项里做。
      新增用例：`--keep-config` → 断言配置被保留；`--delete-config` → 断言配置被删。
      **结果（2026-10-17）**：完成（commit ae86b51）。
      - **修法选 (a)**：让 `Program.cs` **显式支持 `--keep-config`**（幂等），`test.bat:68`
        保持不变——显式优于隐含，测试脚本从此表达真实意图。契约：默认保留 `~/.claude`；
        `--delete-config` 请求删除；`--keep-config` 把「保留」显式化（可覆盖前面的
        `--delete-config`）；两者都给时以命令行中**最后出现的**那个为准（原实现是
        「`--delete-config` 一出现就删」，无顺序概念——这是本项**唯一的行为变更**，
        只影响同时给出两个开关的畸形输入；`test.bat` 从不同时给）。
      - **实现**（`src\Program.cs`，C# 5）：抽纯函数 `ParseUninstallArgs(string[])`
        → `internal struct UninstallArgs { bool KeepConfig; string TargetDir; }`
        （`--target-dir=` 取第一个匹配，与旧 `ArgValue` 一致），`RunSilentUninstall`
        改调它；`InstallerCore.Uninstall(ua.TargetDir, null, ua.KeepConfig, out err)`。
      - **顺手项：做了**（判断为「改动面不大」）。新增纯函数 `UnknownArgs(args, flags, prefixes)`
        返回未识别的开关，`Main` 在三个静默模式（selftest/install/uninstall）各调一次
        `WarnUnknownArgs` 打一行「警告: 忽略无法识别的参数: …」。未知开关不再被静默吞掉。
        已知集按模式定义（`SelfTestFlags/InstallFlags/UninstallFlags` + `--target-dir=`/`--report=`
        前缀）；GUI 模式不解析命令行开关，未纳入。`test.bat` 的三条命令行开关全在已知集内
        → 不产生警告、不改变其输出。
      - **单测**（`tests\UnitTests.cs`，新增 6 用例 / 12 断言）：`--keep-config`→保留、
        `--delete-config`→删除、无开关→默认保留、`--delete-config --keep-config`→保留
        （后置覆盖）、`--target-dir=` 抽取、**`test.bat:68` 那条完整命令行未知开关数=0**
        （直接锁住「`--keep-config` 被识别」）、笔误 `--keep-confgi` 被报为未知。
        特别说明：**「`--keep-config`→保留」本身不判别**（保留是默认值，改前改后都绿），
        真正判别修复的是「`--keep-config` 不再算未知开关」与「可覆盖前置 `--delete-config`」
        这两条断言。`build\unit.bat` **40 用例 / 105 断言全绿，退出码 0**（AW-4 为 34/93）。
      - **撤掉修复确认变红（定点破坏）**：只把 `UninstallFlags` 里的 `--keep-config` 删掉、
        并把 `ParseUninstallArgs` 里对应的 `else if` 分支删掉（复现原 bug，不碰其他任何改动），
        `grep` 确认工作树那两处确实已无 `--keep-config` 后重跑 →
        **2 个用例变红（`ParseUninstallArgs_ExplicitKeepOverrides`、
        `UnknownArgs_RecognisesRealCommandLine`）、2 个断言失败、退出码 1**；
        还原后 `grep` 确认两处均已恢复、重跑 **105/105 全绿**（先确认回退生效再取信）。
      - **红线**：本项只跑 `unit.bat`，**未跑 E2E、未触碰 `HKCU\Environment`**（无 `%TEMP%`
        残留）；未引入任何第三方依赖；源码保持 C# 5（struct + 纯函数，无新语法）；
        显式 `ls` 确认 `src\BuildInfo.cs` 仍在（858 字节，未被 stub 污染）。**无卡点**。

- [x] **AW-6 修「原子落盘」不原子 + Rollback 误删既有安装（健壮性）**
      问题两处：
      (a) `src\Installer.cs:132-137` 先 `SafeDelete(exePath)` 再 `File.Move(tmpPath, exePath)`；
      `Move` 失败时**旧的 claude.exe 已经没了**，与 `README.md:71` 的「原子落盘」表述不符。
      (b) `src\Installer.cs:502-512` 的 `Rollback` 会 `Directory.Delete(dir, true)` 整个安装目录；
      在「覆盖/修复安装」场景下中途失败，会**把用户原有的安装一并删掉**。
      修法：(a) 改为先 `File.Move(exePath, exePath + ".old")` 备份、再落盘、成功后才删 `.old`，
      失败则回滚 `.old`；(b) Rollback 前先判断本次安装**是否是覆盖安装**，
      是覆盖就恢复到备份状态而不是整体删除。
      ⚠️ 这两处改动风险较高，**先读完整上下文再动手**，并在条目里写清失败场景下用户会看到什么。
      新增用例：模拟 `Move` 失败 → 断言旧 exe 仍在；模拟覆盖安装中途失败 →
      断言原有安装未被删除。
      **结果（2026-10-17）**：完成（commit cf17d59）。
      - **契约 (a)**：落盘改为**先备份后落盘**——把已存在的旧 `claude.exe` 改名为
        `claude.exe.old`，再把新产物 Move 到位；Move 失败时把 `.old` 改回原名，用户原有的
        `claude.exe` **原样保留**。备份**不在落盘处删除**，而是保留到安装整体成功才提交删除
        （`CommitBackup`），因此它同时是覆盖安装的事务回滚点（供 (b) 用）。
      - **契约 (b)**：安装开始**之前**判定本次是否为「覆盖/修复安装」
        （`IsOverwriteInstall`：安装前目录里已有 `claude.exe` 或 `manifest.json`），
        结果一直传到回滚点。`Rollback` 改为调 `RollbackDir`：**全新安装**才删除安装目录；
        **覆盖安装绝不整体删除**，而是把 `claude.exe.old` 还原回 `claude.exe`（撤销本次覆盖），
        无备份则原样保留。另：失败但尚未写系统改动时（如步骤 5 冒烟测试失败，此时 `touchedSystem`
        仍为 false）也还原 `.old`，避免留下残留。
      - **失败场景下用户会看到什么**：
        - (a) 覆盖安装时新产物落盘失败（如旧 exe 被占用/杀软扫描）：安装**报错退出**
          （`安装失败: …` + 具体异常），但用户原有的 `claude.exe` **仍在原位、内容不变**，
          命令行 `claude` 照常可用——不再出现「装到一半旧版也没了」的空窗。
        - (b) 覆盖/修复安装中途失败：安装报错退出并**保留用户的既有安装目录**（含原有
          `claude.exe`，若有备份则已还原），只回滚本安装器本次写的系统改动
          （PATH / 快捷方式 / 卸载注册项）；日志打印「回滚完成：保留既有安装（覆盖安装，
          未删除目录；已还原备份）」。全新安装失败则仍按旧行为删除本次创建的目录
          （打印「回滚完成：已删除本次创建的安装目录」）。
      - **可测性（AW-3/4/5 同款）**：抽出窄接口 `IFileOps`（`FileExists/DirExists/MoveFile/
        DeleteFile/DeleteDir`，生产实现 `RealFileOps`），把「落盘失败」「覆盖回滚」这些
        **只在异常路径发生**的分支变成可注入、可确定性驱动的逻辑（注入一个会让 Move 抛异常的假
        fs 即可，不必真造磁盘故障）。核心逻辑抽成可测的
        `AtomicPlace` / `CommitBackup` / `RestoreOverwriteBackup` / `RollbackDir` /
        `IsOverwriteInstall`。
      - **单测**（`tests\UnitTests.cs`，新增 5 用例 / 21 断言，含内存 `FakeFileOps`）：
        `AtomicPlace_MoveFails_OldExeKept`（模拟落盘 Move 失败 → 旧 exe 仍在、内容为旧值、
        `.old` 无残留）、`AtomicPlace_Success_KeepsBackupUntilCommit`、`Rollback_Overwrite
        KeepsExistingInstall`（模拟覆盖安装中途失败 → 目录未被删、旧 exe 从备份还原；另覆盖
        「尚无备份」的子场景）、`Rollback_FreshInstallDeletesDir`、`IsOverwriteInstall`。
        `build\unit.bat` **45 用例 / 126 断言全绿，退出码 0**（AW-5 为 40/105）。
      - **撤掉修复确认变红（定点破坏，两轮）**：
        ① (a)：把 `AtomicPlace` 失败分支里的 `fs.MoveFile(oldPath, exePath)` 删掉（复现
        「落盘失败不还原」），`grep`/`sed` 确认工作树那处已无还原调用后重跑 →
        **`AtomicPlace_MoveFails_OldExeKept` 2 个断言变红、退出码 1**；还原后重跑全绿。
        ② (b)：把 `RollbackDir` 的 `if (wasOverwrite)` 改成 `if (wasOverwrite && false)`
        （复现「无条件删目录」），`grep` 确认改动存在后重跑 →
        **`Rollback_OverwriteKeepsExistingInstall` 4 个断言变红、退出码 1**；还原后
        `grep` 确认 `break-check` 计数为 0、重跑 **126/126 全绿**（先确认回退生效再取信）。
      - **红线**：本项只跑 `unit.bat`（它已编译全部产品源码，等于全项目编译检查），
        **未跑 E2E、未触碰 `HKCU\Environment`**（无 `%TEMP%` 残留）；未引入任何第三方依赖；
        源码保持 C# 5（interface + 纯函数/`out`，无新语法）；显式 `ls` 确认 `src\BuildInfo.cs`
        仍在（858 字节，未被 stub 污染）。README:71「原子落盘」与 77「自动回滚」的表述
        现在名副其实，无需改文档。**无卡点**。

- [x] **AW-7 清理死代码与冗余判定（打磨）**
      四处（`src\MainForm.cs:43` 的 `_logPath` 只在 511 行赋值、全文件从未读取；
      `src\MainForm.cs:516-524` 的 `if (_installDone && …StartsWith("完"))` 分支与随后的
      `Close()` 行为相同；`src\Checks.cs:198-204` 的 `if (build < 10240)` 被紧随其后的
      `if (build < 17763)` 完全覆盖；`.gitignore:6` 与 `.gitignore:25` 重复列出 `build/work/`）。
      修法：逐处删除，**每删一处都要确认没有行为变化**（编译通过 + 单测全绿）。
      `src\Checks.cs:198-204` 那处要特别小心：确认 `build < 10240` 分支里
      **没有任何副作用**（比如设置状态、写日志）才能删；有副作用就保留并改成能说明意图的形式。
      新增用例：删掉 `Checks.cs` 冗余分支后，断言 OS 版本判定的边界行为不变
      （build = 10240 / 17763 / 17762 三档）。
      **结果（2026-10-17）**：完成（commit cce31a1）。四处处理如下：
      - **① `src\MainForm.cs` 的 `_logPath`**：删除字段声明（旧 43 行）**及它唯一的赋值处**
        （旧 508-513 的 `try { ... File.Exists(install.log) ... } catch {}` 整块）。
        `grep _logPath` 全仓只剩这两处（声明 + 赋值），**全文件从未读取**，是纯死代码。
        被删的 try 块只做 `File.Exists` 后赋给该字段，无 I/O、无副作用；`Util.InstallDir()`
        是纯 getter。删除后无行为变化（编译通过 + 单测全绿）。
      - **② `src\MainForm.cs` 的 `OnSecondary`**：删除
        `if (_installDone && _btnSecondary.Text.StartsWith("完", …)) { Close(); return; }`，
        只留 `Close();`——原分支与随后的 `Close()` 行为完全相同，是冗余判定（条件只读字段，
        无副作用）。`_installDone` 在别处（219/366/499 行）仍被使用，未变孤儿。
      - **③ `src\Checks.cs` 的 `if (build < 10240)`：判断为「不是纯冗余分支」，按规则保留**
        （**有副作用**）。理由：它**并非被紧随其后的 `if (build < 17763)` 完全覆盖**——
        `State`（都是 Fail）与 `Detail` 相同，但 **`Advice` 文案不同**：
        `build < 10240`（Windows 10 之前，如 Win7/8/8.1）是
        「Claude Code 需要 Windows 10 1809 (build 17763) 或更高版本。」，
        `10240..17762`（Win10 但过旧）是「版本过旧，需要 Windows 10 1809…」。
        即该分支**设置了返回项的状态并提前 return，且 Advice 与下一条不同**（可观察的副作用）；
        删掉会改变 `build < 10240` 时用户看到的提示文案。故按「有副作用就保留并改成能说明
        意图的形式」处理：把判定逻辑抽成**纯函数 `Preflight.OsVerdict(int build, string name)`**
        （AW-3/AW-4 同款套路），`CheckOs` 只负责读注册表后调它；两档合成一个
        `if (build < 17763)` 块，按 `build < 10240` 选 Advice，并加注释说明它为何不是可删的
        冗余分支。**行为逐字保持**（三档 Advice/State/Detail 与改前一致）。
      - **④ `.gitignore`**：删除第 25 行重复的 `build/work/`（第 6 行「构建中间产物」已列出），
        ignore 语义不变。
      - **新增用例**（`tests\UnitTests.cs`，2 用例 / 8 断言）：
        `Test_OsVerdict_Boundaries`（build=10240→Fail、17762→Fail、17763→Pass，
        并断言 detail 带 build 号）与 `Test_OsVerdict_PreWindows10Advice`
        （pre-Win10（9600）的 Advice 含 ASCII 标记 `"Claude Code"`、10240 的 Advice 不含，
        锁住两档文案区别）。测试文件保持 ASCII-only（用 ASCII 标记而非中文字面量）。
      - **用例数**：`build\unit.bat` **47 用例 / 134 断言全绿，退出码 0**（AW-6 为 45/126）。
      - **撤掉修复确认变红（定点破坏，两轮，每轮先确认回退生效）**：
        ① 模拟「删掉 `build < 10240` 分支」（把两档 Advice 合一为「版本过旧…」），
        `grep` 确认工作树里已无 `if (build < 10240)` 代码行、且「Claude Code 需要」那行已消失
        （回退生效）→ **`OsVerdict_PreWindows10Advice` 1 断言变红、退出码 1**
        （**证明删掉它确有行为变化 → 印证「保留」的决定**）；还原后 `grep` 确认已恢复、
        重跑 **134/134 全绿**。
        ② 把外层阈值 `build < 17763` 改成 `build < 10240`，`sed` 确认工作树已生效 →
        **`OsVerdict_Boundaries` 2 断言变红、退出码 1**；还原后 `grep` 确认外层回到 17763、
        重跑 **134/134 全绿**（先确认回退生效再取信）。
      - **红线**：本项只跑 `unit.bat`（它编译全部产品源码 = 全项目编译检查），
        **未跑 E2E、未触碰 `HKCU\Environment`**（无 `%TEMP%` 残留）；未引入任何第三方依赖；
        源码保持 C# 5（纯函数，无新语法）；测试文件 ASCII-only
        （`LC_ALL=C grep '[^ -~]'` = 0 命中）；显式 `ls` 确认 `src\BuildInfo.cs` 仍在
        （858 字节，未被 stub 污染）。**无卡点**。

- [x] **AW-8 把 `docs\PLAN.md` 与实现对齐（文档）**
      四处不一致：
      (a) `docs\PLAN.md:87` 写「目标卷 ≥ 600 MB」，`src\Common.cs:35` 与 `README.md:58` 是 700 MB；
      (b) `docs\PLAN.md:134-135` 把 `Installer.cs` / `Uninstaller.cs` 列在 `build/` 下，实际在 `src/`；
      (c) `docs\PLAN.md:137` 提到 `build\langtest.bat`「已保留」，但该文件**不存在**；
      (d) `docs\PLAN.md:63` 说安装目录含「官方 zip 内其余文件」，
      实际安装器只解出 `claude.exe`（`src\Installer.cs:41` `PayloadEntry = "claude.exe"`）。
      修法：以**代码为准**改文档（不要改代码去迁就文档）。
      `langtest.bat` 那句要么删掉、要么改成「历史上用过，已移除」——**不要凭空造一个脚本出来**。
      验证方式：逐条对照 `file:line` 复核，并在条目里列出「文档旧值 → 新值」的对照表。
      **结果（2026-10-17）**：完成（commit e9f82f8）。
      - **文档定位判断：`docs/PLAN.md` 是「活文档/设计规格」而非历史存档**，故按「以代码为准
        逐条改文档」处理（而非只在开头加「历史计划」声明）。理由两条：
        ① 它是被**维护过**的——§7「已确认事项」、§8「实测结果」都是实现完成之后追加的
        （不是一次写死的计划快照），说明随项目演进更新；
        ② 前序条目**把它当权威引用**——AW-2 用 `docs\PLAN.md:120`、AW-3 用 `docs\PLAN.md:110`
        与 README 并列，用来判定「是代码错还是文档错」，即项目自身把 PLAN.md 的表述视为
        应与代码一致的规格。据此，四处不一致都是「文档该改」。
        （稳妥起见，另在文首加了一句「文中若与代码不一致，以 `src/` 下的实现为准」的说明行，
        给后来者一个明确优先级，避免再次漂移。）
      - **文档旧值 → 新值 对照表**（均已按 `file:line` 复核代码为准）：
        | 项 | 位置 | 旧值 | 新值 |
        |---|---|---|---|
        | (a) | `docs\PLAN.md` §3 表格第 3 行 | `目标卷 ≥ 600 MB` | `目标卷 ≥ 700 MB` |
        | (b) | `docs\PLAN.md` §4 目录树 | `Installer.cs`/`Uninstaller.cs` 列在 `build/` 下 | 改列在 `src/` 下（`build/` 下保留 `build.bat`） |
        | (c) | `docs\PLAN.md` §4 目录树 | `└── langtest.bat  # C#5 能力自检（已保留）` | **删掉该行**（文件不存在，不凭空造） |
        | (d) | `docs\PLAN.md` §2.2 磁盘布局 | `├── (官方 zip 内其余文件)` | **删掉该行**，并在 `claude.exe` 行注明「（安装器只解出这一个文件）」 |
      - **代码依据（复核）**：`src\Common.cs:35` = `RequiredFreeBytes = 700L * 1024 * 1024`；
        `README.md:58` = `≥ 700 MB`；`Installer.cs`/`Uninstaller.cs` 实际在 `src/`（`ls src/` 确认，
        `build/` 下只有 `build.bat`/`test.bat`/`unit.bat`/`e2e-with-backup.bat` 等脚本）；
        `src\Installer.cs:74` `PayloadEntry = "claude.exe"`，解包只 `ExtractEntry(..., PayloadEntry, ...)`
        （`:114`/`:150`），安装目录不含 zip 内其余文件；全仓 `find -iname '*langtest*'` 无命中。
      - **验证**：`build\unit.bat` **47 用例 / 134 断言全绿，退出码 0**（纯文档改动，未动代码）。
        显式 `ls src\BuildInfo.cs` 确认仍在（858 字节，未被 stub 污染）。
      - **红线**：本项**纯文档改动**，未跑 E2E、未触碰 `HKCU\Environment`；未引入任何依赖；**无卡点**。

- [x] **AW-9 给 `docs\PLAN.md` 里的个人绝对路径脱敏（隐私）**
      问题：`docs\PLAN.md:30` 含本机 Git 安装路径 `E:\Git`、`E:\Git\bin\bash.exe`
      （`docs\PLAN.md:34` 还有「C: 可用空间 347 GB」）。这是**唯一进入版本库的个人环境信息**。
      修法：改成占位符（如 `<Git 安装目录>\bin\bash.exe`），并把「347 GB 可用空间」
      这类本机快照数据改成中性表述或删掉。**注意**：`docs/PLAN.md` 是文档，
      脱敏不要改变技术含义。
      验证方式：`git grep -nE '[A-Z]:\\\\' -- docs/` 应不再命中真实个人路径
      （剩下的是示例占位符）。
      **结果（2026-10-17）**：完成（commit b87d7d4）。
      - **脱敏对照（旧值 → 新值，均在 `docs\PLAN.md` 的「本机构建环境」表；AW-8 加过文首
        说明行后行号由 30/34 变为 33/37）**：
        | 位置 | 旧值 | 新值 |
        |---|---|---|
        | 表行 `Git for Windows` | `✅ 2.45.1 @ \`E:\Git\`（bash: \`E:\Git\bin\bash.exe\`）` | `✅ 2.45.1 @ \`<Git 安装目录>\`（bash: \`<Git 安装目录>\bin\bash.exe\`）` |
        | 表行 `C: 可用空间` | `347 GB` | `充足（满足安装所需）` |
      - **技术含义未变**：只把真实盘符路径换成占位符、把本机磁盘快照换成中性表述；
        保留了 Git for Windows 2.45.1 可用、bash 位于 `<Git 安装目录>\bin\bash.exe` 这两条
        事实（占位符仍表达了「bash 在 Git 安装目录下的 bin\ 里」这一技术结构）。
      - **验证（`git grep` 实际输出）**：改前 `git grep -nE '[A-Z]:\\\\' -- docs/` 命中
        `docs/PLAN.md:33:...@ \`E:\Git\`（bash: \`E:\Git\bin\bash.exe\`）`；改后同命令
        **无任何输出、退出码 1**（不再命中真实个人路径；占位符不含盘符故也不命中）。
        另扫 `347 GB`/`E:\\`/`E:/` 亦**零命中**。确认 docs/ 下已无本机个人环境信息。
      - **单测**：`build\unit.bat` **47 用例 / 134 断言全绿，退出码 0**（与 AW-8 基线一致，
        纯文档改动未动代码）。显式 `ls src\BuildInfo.cs` 确认仍在（858 字节、mtime 未变，
        未被 stub 污染）；`git status` 仅 `docs/PLAN.md` 变更。
      - **红线**：本项**纯文档改动**，未跑 E2E、未触碰 `HKCU\Environment`；未引入任何依赖；
        未碰 `main`、未 force push。**无卡点**。

- [x] **AW-10 修 ARM64 下载分支的临时文件泄漏（健壮性）**
      问题：`src\Installer.cs:83-84`、`234` 把 ARM64 载荷下载到 `%TEMP%`，
      仅在 `finally` 里删除；进程被杀（用户取消、任务管理器结束、断电）就残留约 **96 MB** 临时文件。
      修法：用可预测的固定文件名 + **启动时清理上一轮残留**（在下载前先删同名旧文件），
      或改用 `FileOptions.DeleteOnClose` 之类能在崩溃后回收的机制。
      注意 C# 5 + .NET Framework 4.x 的可用 API 范围。
      新增用例：把「临时文件命名/清理」抽成可测的纯逻辑，断言命名可预测、
      且「下载前清理旧文件」这条路径被走到。
      **结果（2026-10-17）**：完成（commit 845f305）。
      - **现状定位（行号按当前文件）**：旧实现 `src\Installer.cs:123-124` 把 ARM64 载荷下载到
        `%TEMP%\claude-arm64-<随机8位>.zip`（`Guid.NewGuid()` 取名），且**只在 `finally`**
        （旧 `:286`）删除。`finally` 只在**正常退出**时运行——用户取消 / 任务管理器结束 /
        断电时不会执行，约 96 MB 残留；而**随机名**意味着下一轮安装**无法定位**它，残留永远无人回收。
      - **选的机制：可预测的固定文件名 + 下载前清理上一轮残留**（而非 `DeleteOnClose`）。
        理由（写进代码注释）：`DeleteOnClose` 只在**文件句柄关闭**时删除，而下载
        （`Net.DownloadFile` 按**路径**写）与解压（`ZipFile.OpenRead` **二次打开**）分属不同句柄；
        要让「崩溃后回收」成立就得让该句柄活到解压结束，改动面远大于固定名 + 预清理，且
        `FileShare` 语义会让二次打开失败。固定名 + 下载前清理改动小、可测，且与仓库既有套路一致。
      - **命名/清理规则**：固定名 `claude-arm64-payload.zip`（常量 `Arm64TempZipName`，不含随机数）；
        路径由纯函数 `Arm64TempZipPath(tempDir)` 计算。安装进入 ARM64 分支、**下载之前**调
        `PrepareArm64TempZip(Path.GetTempPath(), new RealFileOps())`：先算固定路径、再
        `CleanStaleTempZip` 删同名旧文件（存在则删、返回 true），然后才交给 `Net.DownloadFile` 写。
        正常路径仍由 `finally` 删除，两条路径互补。
      - **实现**（`src\Installer.cs`，C# 5）：新增常量 `Arm64TempZipName` + 三个纯函数/窄逻辑
        `Arm64TempZipPath` / `CleanStaleTempZip(path, IFileOps)` / `PrepareArm64TempZip(tempDir, IFileOps)`；
        复用 AW-6 已有的窄接口 `IFileOps`（注入 `FakeFileOps` 即可在内存里驱动，**不真下载 96 MB**）；
        ARM64 分支改用 `PrepareArm64TempZip` 并记一行日志。无新语法（无插值 / `?.` / 表达式体）。
      - **单测**（`tests\UnitTests.cs`，新增 3 用例 / 9 断言，全部用注入的 `FakeFileOps`，
        **不碰真实 `%TEMP%`、不下载任何字节**，测试文件保持 ASCII-only）：
        `Arm64TempZipPath_Predictable`（两次调用结果相同＝无随机 GUID；等于
        `C:\Temp\claude-arm64-payload.zip`；以 `.zip` 结尾；文件名与 tempDir 无关）、
        `CleanStaleTempZip`（无残留→返回 false；有残留→返回 true 且文件消失）、
        `PrepareArm64TempZip_CleansLeftover`（在固定路径放一个「上一轮被杀残留」→ 调生产函数后
        断言返回固定路径、且**残留已被删除**——即「下载前清理旧文件」这条路径被**生产代码**走到）。
        `build\unit.bat` **50 用例 / 143 断言全绿，退出码 0**（AW-9 为 47/134）。
      - **撤掉修复确认变红（定点破坏，两轮，每轮先确认回退生效）**：
        ① **命名回归**：把 `Arm64TempZipPath` 改回旧的随机 GUID 名（复现原 bug），
        `grep` 确认工作树那行确含 `Guid.NewGuid()...".zip"` 后重跑 →
        **5 个断言变红（`Arm64TempZipPath_Predictable` 3 条 + `PrepareArm64TempZip_CleansLeftover`
        2 条）、退出码 1**；还原后 `grep` 确认已无 GUID、重跑 **143/143 全绿**。
        ② **清理路径回归**：把 `PrepareArm64TempZip` 里的 `CleanStaleTempZip(path, fs);`
        调用删掉（复现「下载前不清理」），`sed` 确认工作树该行已消失后重跑 →
        **`PrepareArm64TempZip_CleansLeftover` 1 断言变红、退出码 1**；还原后 `sed` 确认该行已回、
        重跑 **143/143 全绿**（先确认回退生效再取信）。
      - **红线**：本项只跑 `unit.bat`（它编译全部产品源码 = 全项目编译检查），**未跑 E2E、
        未触碰 `HKCU\Environment`**（无 `%TEMP%` 残留，测试全程用内存假文件系统）；
        未引入任何第三方依赖；源码保持 C# 5；`tests\UnitTests.cs` ASCII-only
        （`LC_ALL=C grep -c '[^ -~]'` = 0）；显式 `ls` 确认 `src\BuildInfo.cs` 仍在
        （858 字节、mtime 未变，未被 stub 污染）。**无卡点**。

- [x] **AW-11 给空 catch 块补最小可观测性（健壮性，需限定范围）**
      问题：全仓有 **38 处空 catch**（`src\Common.cs:376,397,417,520,646-647`、
      `src\Installer.cs:215,234,334,394,405,497,505-510`、`src\Program.cs:45,254,265`、
      `src\MainForm.cs:369,513,564` 等）。多数是刻意的防御式编程，
      但会**掩盖真实失败**——尤其卸载路径上的静默失败会让用户以为删干净了。
      ⚠️ **不要 38 处一起改**，那样无法验证。这一项**只做安装/卸载主链路上的 catch**：
      `Installer.cs` 里与「删文件 / 删环境变量 / 写 manifest」相关的几处，
      改为至少记一条日志（项目有 `install.log` 机制）或返回失败状态。
      其余位置的 catch 保持不动，在「待确认」区登记「是否要全面加日志」。
      新增用例：模拟这些失败路径 → 断言失败被记录/被返回，而不是无声无息。
      **结果（2026-10-17）**：完成（commit 7784711）。
      - **范围（只动 `src\Installer.cs`，只动「删文件 / 删环境变量 / manifest」这三类的 catch）**：
        改动的站点（按函数）——
        ① `CleanStaleTempZip`（ARM64 临时 zip 清理）；
        ② 安装 `finally` 里删除 ARM64 临时 zip；
        ③ **卸载删环境变量**（`Uninstall` 里 CLAUDE_CODE_GIT_BASH_PATH 那段，旧空 catch）★核心；
        ④ 卸载删空的开始菜单目录；
        ⑤ `AtomicPlace` 清旧 `.old` + 落盘失败还原 `.old`（两处）；
        ⑥ `CommitBackup`（提交时删 `.old`）；⑦ `RestoreOverwriteBackup`（两处）；
        ⑧ `RollbackDir`；⑨ `Rollback` 辅助（6 处）；
        ⑩ `ReadGitBashManifest`（manifest 读取）；⑪ `SafeDelete` / `DeleteIfExists`。
        说明：`Installer.cs` 里**没有**「写 manifest」的 catch——`WriteManifest` 不吞异常，
        写失败会一路抛到安装主 catch → `r.Ok=false`（已经是「返回失败状态」），故无可改。
      - **做法（沿用仓库套路：可注入窄接口 + 纯函数单测）**：
        新增 `TryDeleteFile` / `TryDeleteDir`（复用 AW-6 的 `IFileOps`，失败记日志并返回 false）、
        可注入委托 `EnvVarRemover` + `DefaultEnvRemover` + `RemoveGitBashEnvIfOwned`
        （复用 AW-1 的 `ShouldRemoveGitBashEnv` 判定归属，移除失败记日志并返回 false）；
        `SafeDelete`/`DeleteIfExists` 改为走 `TryDeleteFile`（不再各自空 catch）。
        `RollbackDir` 顺带**修正返回值**：旧实现删除失败也 `return true`，会让调用方谎报
        「已删除目录」；现在失败返回 false，`Rollback` 也据此打印「删除失败，目录可能残留」。
        其余站点把空 `catch { }` 改成 `catch (Exception ex) { Log.Write(...); }`（走 `install.log` 机制）。
      - **未动（保持原样，登记待确认）**：`Installer.cs` 里**非这三类**的空 catch——
        install.log 合并、`Verify` 读注册表、`LooksLikeClaudeHome` 探测、
        `ReadUserPathRaw`/`ReadUserPathKind`、`RunCapture` 的 `p.Kill()`；
        以及 `Common.cs`/`Program.cs`/`MainForm.cs`/`Checks.cs` 的全部 catch。
        即「38 处」里除本项列出的站点外**全部未动**。
      - **单测**（`tests\UnitTests.cs`，新增 12 用例 / 35 断言，含新 `ThrowingFileOps`——
        可让指定的 `DeleteFile`/`DeleteDir`/`MoveFile` 抛异常，以及 `LoggedSince` 断言「新日志里出现某标记」）：
        `TryDeleteFile_LogsOnFailure`、`TryDeleteDir_LogsOnFailure`、
        `RemoveGitBashEnvIfOwned_RemovesOwnedValue`/`_LogsRemovalFailure`/`_KeepsUserValue`/`_Absent`、
        `CommitBackup_LogsOnFailure`、`RestoreOverwriteBackup_LogsOnFailure`、
        `RollbackDir_FreshInstallDeleteFails_ReportsNotDeleted`、
        `AtomicPlace_RestoreFails_LogsBoth`、`ReadGitBashManifest_LogsOnReadFailure`
        （用 `FileShare.None` 锁住 manifest 让 `File.ReadAllText` 确定性地抛 IOException）、
        `CleanStaleTempZip_LogsOnFailure`。测试文件保持 ASCII-only（`LC_ALL=C grep -c '[^ -~]'` = 0）。
        `build\unit.bat` **62 用例 / 178 断言全绿，退出码 0**（AW-10 为 50/143）。
      - **撤掉修复确认变红（定点破坏，两轮，每轮先确认回退生效）**：
        ① **删环境变量失败路径**：把 `RemoveGitBashEnvIfOwned` 的失败分支从
        `catch (Exception ex) { Log.Write(...); return false; }` 改回旧的 `catch { return true; }`
        （复现「静默吞掉、还报成功」），`grep` 确认工作树那行确实是 `catch { return true; }` 后重跑 →
        **`RemoveGitBashEnvIfOwned_LogsRemovalFailure` 2 个断言变红、退出码 1**；
        还原后 `grep` 计数为 0、重跑 **178/178 全绿**。
        ② **删文件记日志**：把 `TryDeleteFile` 的失败分支改回 `catch { return false; }`（静默），
        `sed` 确认工作树该行已生效后重跑 → **`TryDeleteFile_LogsOnFailure` 1 个断言变红、退出码 1**；
        还原后 `grep` 计数为 0、重跑 **178/178 全绿**（先确认回退生效再取信）。
      - **红线**：本项只跑 `unit.bat`（它编译全部产品源码 = 全项目编译检查），**未跑 E2E、
        未触碰 `HKCU\Environment`**（无 `%TEMP%` 残留，测试全程用内存假文件系统 + 进程内注入委托）；
        未引入任何第三方依赖；源码保持 C# 5（委托 + 纯函数/`out`，无新语法）；
        显式 `ls` 确认 `src\BuildInfo.cs` 仍在（858 字节、mtime 未变，未被 stub 污染）。**无卡点**。

- [ ] **AW-12 加 CI（打磨）**
      问题：没有 `.github/workflows/`，改动没有任何自动把关。
      修法：加一个 workflow，push 时在 **`windows-latest`** runner 上
      跑 `build\build.bat`（或至少编译 + `build\unit.bat`）与 `--selftest`。
      ⚠️ **不要**把 `build\test.bat` 放进 CI：它会写 211 MB、装到沙箱、
      并且依赖卸载行为（还有 AW-1 那类会动环境变量的历史），在 CI 里风险与收益不成比例。
      ⚠️ CI 里不要下载 96 MB 的 ARM64 载荷（网络依赖，会 flaky）。
      验证方式：本地先跑通同样的命令，push 后用 `git credential fill` 取 token
      调 GitHub API 查这次 workflow run 的结果；**查不到就如实说「已推送但未能确认 CI 结果」**，
      不要假装绿了。

## 待确认（不要擅自做，等用户拍板）

- **32 位 Windows 怎么处理？** AW-4 默认按「拒绝安装」实现。如果这台安装器有意允许
  32 位系统（例如用户环境里真有 32 位机器），应改为「警告 + 允许继续」。
- **卸载时那个环境变量到底该不该删？** AW-1 按「只在由本安装器写入时才删」实现
  （兑现代码注释的契约）。如果你其实想要「卸载就删干净」，那是另一个契约，说一声。
  另：若用户**安装前**已自配该变量、被安装器覆盖，卸载会按契约删除（用户原值丢失）。
  要不要改成「安装时记录用户原值、卸载时还原」？属产品决策，等拍板。
- **38 处空 catch 要不要全面加日志？** AW-11 只做了 `Installer.cs` 里与「删文件 / 删环境变量 /
  manifest」相关的那些（见 AW-11 结果列出的站点），其余全部保持不动。仍待拍板的是：
  要不要给**其余**的空 catch 也全面加日志——`Installer.cs` 的 install.log 合并 / `Verify` 读注册表 /
  `LooksLikeClaudeHome` 探测 / `ReadUserPathRaw`·`ReadUserPathKind` / `RunCapture` 的 `p.Kill()`，
  以及 `Common.cs`（网络/注册表/Git 探测等）、`Program.cs`、`MainForm.cs`、`Checks.cs` 里的全部。
  这些多为刻意的防御式探测，加日志的收益（可诊断性）与噪音需权衡，属产品决策。
- **要不要把 E2E 加进 CI？** AW-12 明确没加，理由是写盘量与网络依赖。
- **`docs/PLAN.md` 是活文档还是历史存档？** —— **AW-8 已判定为「活文档/设计规格」并据此
  逐条改文档对齐代码**（理由见 AW-8 结果：§7/§8 是实现后追加、且 AW-2/AW-3 把它当权威引用）。
  若你其实希望它冻结成「当时的计划」，说一声，可改为在文首声明「历史计划，实现以代码为准」。
- **要不要给仓库加 LICENSE？** 目前没有（`VERSION` 是 2.1.268，跟着上游版本走）。

## 收尾

队列清空（全部 `[x]` 或 `BLOCKED`）后**停下来**，在文件末尾写一节「## 作业总结」：
逐条列出完成了什么、验证结果（用例数）、`BLOCKED` 项及原因、待确认项，
并特别注明 **E2E 运行期间 `HKCU\Environment` 有没有被改动、是否已恢复**。
然后向用户报告。**不要自己发明新条目继续做。**
