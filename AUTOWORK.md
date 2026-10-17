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

- [ ] **AW-3 修预检文案与实现相反（正确性）**
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

- [ ] **AW-4 修 32 位 Windows 被误判为 x64（正确性）**
      问题：`src\Checks.cs:228` 把 `"AMD64"` 与 `"X86"` 合并成同一分支，
      返回 Pass、`UseBundled=true`，文案「x64 — 使用内置载荷」。
      32 位 Win10（`PROCESSOR_ARCHITECTURE=X86`）会通过 OS 检查（build ≥ 17763）
      与架构检查，然后**装上一个 64 位 claude.exe**，直到冒烟测试才失败。
      修法：把 `X86` 从 x64 分支拆出来，返回 Fail 或 Warn（32 位不受支持）。
      ⚠️ **产品决策**：「明确拒绝」还是「警告后仍允许继续」属于用户决策。
      默认按**拒绝（Fail）**处理并在「待确认」区登记；如果你判断原设计有意允许，
      就记到待确认、不要擅自改。
      新增用例：`X86` → 断言不再返回 Pass；`AMD64` → 断言仍 Pass。

- [ ] **AW-5 修 `--keep-config` 无效开关（正确性，测试脚本的自欺）**
      问题：`build\test.bat:68` 传 `--keep-config`，但 `src\Program.cs:195` 只识别
      `--delete-config`（保留才是默认值），`--keep-config` 被**静默忽略**，
      测试脚本给人「显式保留了配置」的错觉。
      修法：二选一——(a) 让 `Program.cs` 显式支持 `--keep-config`（幂等、明确），
      或 (b) 从 `test.bat` 删掉它。**推荐 (a)**：显式优于隐含，且能让测试脚本表达真实意图。
      顺手：`Program.cs` 遇到**未知开关**时应该给出警告而不是静默忽略（如果现在就是静默忽略），
      这样以后类似的笔误不会再被吞掉。这条如果改动面大，单独记为后续条目，别在这项里做。
      新增用例：`--keep-config` → 断言配置被保留；`--delete-config` → 断言配置被删。

- [ ] **AW-6 修「原子落盘」不原子 + Rollback 误删既有安装（健壮性）**
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

- [ ] **AW-7 清理死代码与冗余判定（打磨）**
      四处（`src\MainForm.cs:43` 的 `_logPath` 只在 511 行赋值、全文件从未读取；
      `src\MainForm.cs:516-524` 的 `if (_installDone && …StartsWith("完"))` 分支与随后的
      `Close()` 行为相同；`src\Checks.cs:198-204` 的 `if (build < 10240)` 被紧随其后的
      `if (build < 17763)` 完全覆盖；`.gitignore:6` 与 `.gitignore:25` 重复列出 `build/work/`）。
      修法：逐处删除，**每删一处都要确认没有行为变化**（编译通过 + 单测全绿）。
      `src\Checks.cs:198-204` 那处要特别小心：确认 `build < 10240` 分支里
      **没有任何副作用**（比如设置状态、写日志）才能删；有副作用就保留并改成能说明意图的形式。
      新增用例：删掉 `Checks.cs` 冗余分支后，断言 OS 版本判定的边界行为不变
      （build = 10240 / 17763 / 17762 三档）。

- [ ] **AW-8 把 `docs\PLAN.md` 与实现对齐（文档）**
      四处不一致：
      (a) `docs\PLAN.md:87` 写「目标卷 ≥ 600 MB」，`src\Common.cs:35` 与 `README.md:58` 是 700 MB；
      (b) `docs\PLAN.md:134-135` 把 `Installer.cs` / `Uninstaller.cs` 列在 `build/` 下，实际在 `src/`；
      (c) `docs\PLAN.md:137` 提到 `build\langtest.bat`「已保留」，但该文件**不存在**；
      (d) `docs\PLAN.md:63` 说安装目录含「官方 zip 内其余文件」，
      实际安装器只解出 `claude.exe`（`src\Installer.cs:41` `PayloadEntry = "claude.exe"`）。
      修法：以**代码为准**改文档（不要改代码去迁就文档）。
      `langtest.bat` 那句要么删掉、要么改成「历史上用过，已移除」——**不要凭空造一个脚本出来**。
      验证方式：逐条对照 `file:line` 复核，并在条目里列出「文档旧值 → 新值」的对照表。

- [ ] **AW-9 给 `docs\PLAN.md` 里的个人绝对路径脱敏（隐私）**
      问题：`docs\PLAN.md:30` 含本机 Git 安装路径 `E:\Git`、`E:\Git\bin\bash.exe`
      （`docs\PLAN.md:34` 还有「C: 可用空间 347 GB」）。这是**唯一进入版本库的个人环境信息**。
      修法：改成占位符（如 `<Git 安装目录>\bin\bash.exe`），并把「347 GB 可用空间」
      这类本机快照数据改成中性表述或删掉。**注意**：`docs/PLAN.md` 是文档，
      脱敏不要改变技术含义。
      验证方式：`git grep -nE '[A-Z]:\\\\' -- docs/` 应不再命中真实个人路径
      （剩下的是示例占位符）。

- [ ] **AW-10 修 ARM64 下载分支的临时文件泄漏（健壮性）**
      问题：`src\Installer.cs:83-84`、`234` 把 ARM64 载荷下载到 `%TEMP%`，
      仅在 `finally` 里删除；进程被杀（用户取消、任务管理器结束、断电）就残留约 **96 MB** 临时文件。
      修法：用可预测的固定文件名 + **启动时清理上一轮残留**（在下载前先删同名旧文件），
      或改用 `FileOptions.DeleteOnClose` 之类能在崩溃后回收的机制。
      注意 C# 5 + .NET Framework 4.x 的可用 API 范围。
      新增用例：把「临时文件命名/清理」抽成可测的纯逻辑，断言命名可预测、
      且「下载前清理旧文件」这条路径被走到。

- [ ] **AW-11 给空 catch 块补最小可观测性（健壮性，需限定范围）**
      问题：全仓有 **38 处空 catch**（`src\Common.cs:376,397,417,520,646-647`、
      `src\Installer.cs:215,234,334,394,405,497,505-510`、`src\Program.cs:45,254,265`、
      `src\MainForm.cs:369,513,564` 等）。多数是刻意的防御式编程，
      但会**掩盖真实失败**——尤其卸载路径上的静默失败会让用户以为删干净了。
      ⚠️ **不要 38 处一起改**，那样无法验证。这一项**只做安装/卸载主链路上的 catch**：
      `Installer.cs` 里与「删文件 / 删环境变量 / 写 manifest」相关的几处，
      改为至少记一条日志（项目有 `install.log` 机制）或返回失败状态。
      其余位置的 catch 保持不动，在「待确认」区登记「是否要全面加日志」。
      新增用例：模拟这些失败路径 → 断言失败被记录/被返回，而不是无声无息。

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
- **38 处空 catch 要不要全面加日志？** AW-11 只做了安装/卸载主链路。
- **要不要把 E2E 加进 CI？** AW-12 明确没加，理由是写盘量与网络依赖。
- **`docs/PLAN.md` 是活文档还是历史存档？** 如果它只是「当时的计划」，AW-8 可能不该改它，
  而应该在开头加一句「本文为历史计划，实现以代码为准」。这属于文档定位决策。
- **要不要给仓库加 LICENSE？** 目前没有（`VERSION` 是 2.1.268，跟着上游版本走）。

## 收尾

队列清空（全部 `[x]` 或 `BLOCKED`）后**停下来**，在文件末尾写一节「## 作业总结」：
逐条列出完成了什么、验证结果（用例数）、`BLOCKED` 项及原因、待确认项，
并特别注明 **E2E 运行期间 `HKCU\Environment` 有没有被改动、是否已恢复**。
然后向用户报告。**不要自己发明新条目继续做。**
