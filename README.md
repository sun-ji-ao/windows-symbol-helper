# Windows 符号助手

适用 Windows 10/11 x64：在无法联网的电脑上生成符号需求清单，带到联网电脑下载，再把符号复制到离线调试环境。

## 直接使用

交付程序：[直接下载 SymbolTool.exe](https://github.com/sun-ji-ao/Windows-/raw/refs/heads/main/dist/SymbolTool.exe)。点击链接下载 EXE，保存到可写目录后运行。只复制这个 EXE 即可使用。

备用下载：[SymbolTool.exe 原始文件](https://raw.githubusercontent.com/sun-ji-ao/Windows-/main/dist/SymbolTool.exe)。校验值见 [SHA256SUMS.txt](dist/SHA256SUMS.txt)，第三方声明见 [THIRD-PARTY-NOTICE.txt](dist/THIRD-PARTY-NOTICE.txt)。

程序使用 Windows 自带的 .NET Framework 4.x（需要 4.5 或以上，正常 Windows 10/11 已具备），无需安装 Python、Node、Windows SDK 或另外下载依赖。首次运行把随包微软工具释放到 EXE 同目录 `.symbol-runtime`，再次启动先校验并复用。程序目录必须可写；不要直接在压缩包内运行。

### 1. 在目标离线电脑导出

将 EXE 放在可写目录，右键“以管理员身份运行”，点击 **导出清单**。

生成 EXE 同目录 `request_年月日_时分秒_毫秒`：

- `symbols.manifest`：需要搬到联网电脑的符号清单，不含驱动、PDB、转储或代码。
- `request-info.json`：采集范围、模块及跳过/错误原因，仅供检查。
- `采集说明.txt`：遗漏与警告。

采集已加载的驱动，补充 `Windows\System32\drivers` 顶层 SYS 文件及内核、HAL、KD、Win32k 基础模块。仅保留位于 Windows 目录、版本元数据 CompanyName 包含 Microsoft 的模块；这一筛选用于排除第三方模块，不是数字签名认证，也不保证每一项都在公共服务器发布。仅支持 PDB，不采集私有符号，不扫描完整磁盘，不覆盖所有 Windows 用户态 DLL。

非管理员可以扫描磁盘，但已加载驱动枚举可能失败，会明确记录警告。采集结果取自磁盘模块的 PDB 标识；系统更新待重启、驱动内存版本与磁盘不一致时，先完成重启并重采。不要把“生成了清单”等同于覆盖所有调试模块。

### 2. 在联网电脑下载

运行同一个 EXE，点击 **选择清单并下载**，选择目标离线电脑的 `symbols.manifest`。

生成 EXE 同目录 `symbol_年月日_时分秒_毫秒`。下载严格依据选中清单，不扫描联网电脑。唯一上游为 `https://msdl.microsoft.com/download/symbols`，使用微软符号服务本身的重定向和缓存协议。

每项首次失败后再重试 **3 次，共最多 4 次**。重试前等待 2、4、6 秒；每次 SymChk 调用最多 180 秒。下载错误和 PDB 标识校验失败均进入重试。任何一次成功立即停止该项重试。单项失败不终止后续条目。

下载完成要检查统计：**任务结束不等于全部成功**。每个成功文件必须通过 DbgHelp 读取的 PDB GUID/签名 + Age 匹配校验，并保存 SHA-256。已有缓存也要验证；有历史 SHA-256 的文件还要比对哈希。SHA-256 用于之后检查搬运和存储完整性，并不是微软提供的签名或哈希证明。

### 3. 重试失败项或继续中断的任务

点击 **重试 / 继续下载**：

1. 已完整执行的任务选择符号目录根部 `failed.manifest`，只重试未解决项。
2. 被取消或中断的任务选择原 `symbols.manifest`，已通过校验的文件会复用。
3. 再选择原 `symbol_时间` 目录，避免新建缓存和重复下载。

每次重新运行仍是首次尝试 + 3 次重试。四次都失败后才写最终失败条目；每次尝试的诊断日志仍会保留。空失败清单表示没有待重试项，导入时会提示清单为空。

支持“停止任务”，会停止当前下载进程，保留已完成文件。中断时根部汇总可能尚未更新，应使用原清单继续；`reports` 中 `Complete=false` 表示本轮未完整结束。

### 4. 复制到离线调试环境

复制整个 `symbol_时间` 目录，保留结构：

```text
symbol_时间/
  ntkrnlmp.pdb/<GUID和Age>/ntkrnlmp.pdb
  dxgkrnl.pdb/<GUID和Age>/dxgkrnl.pdb
  symbols.manifest
  failed.manifest
  SHA256SUMS.txt
  reports/<本轮时间>/report.json
  WinDbg使用说明.txt
```

在离线调试环境运行同一个 EXE，点击 **校验符号目录**，检查 PDB 标识与 SHA-256。随后 WinDbg 使用纯本地符号路径，例如：

```text
.sympath srv*D:\Symbols\symbol_20261106_150000_000
.reload /f nt
lmvm nt
.reload /f dxgkrnl.sys
lmvm dxgkrnl
```

请把示例路径换成实际搬运位置。不要平铺 PDB，不要在离线符号路径后附加微软公网地址。只应把目标离线电脑生成的需求清单用于该目标离线电脑；更新系统/驱动后重新采集。

## 报告

`reports/<本轮时间>/report.json` 保存本轮每项状态、尝试次数、最终原因和成功 SHA-256；`failed.manifest` 保存本轮最终失败项。符号目录根部的同名失败清单是历次清单合并后的未解决项。每次网络调用输出保存在 `item-xxxxx-attempt-N.log`，清单中不含任何凭据。

同一个缓存目录同时只允许一个下载任务。清单解析严格限制为 PDB 文件名及十六进制匹配标识，不接受任意下载 URL 或路径穿越内容。

## 构建与验证

源码在 `src\SymbolTool.cs`。在 PowerShell 运行：

```powershell
.\build.ps1
.\tests\Validate.ps1
# 联网实测：会向微软下载一个 Windows version.pdb，并测试不存在的符号四次失败重试
.\tests\Validate.ps1 -Online
```

构建脚本使用系统 .NET Framework C# 编译器，依赖已放入项目自身 `vendor\windows-debug-tools.zip`（取自现有离线工具包的微软 SDK 调试工具）；不安装工具，不修改 PATH，不依赖相邻项目目录。输出 GUI 单文件 `dist\SymbolTool.exe` 及同源码的自动化测试版 `build\SymbolTool.Cli.exe`。也提供 `SymbolTool.csproj`，供安装了相应 Framework 开发组件的 Visual Studio/MSBuild 打开。

命令行测试版支持 `--collect`、`--collect-file <Windows模块>`、`--download <清单> [已有缓存]`、`--verify <缓存>`。退出码 0 表示成功，2 表示未全部下载/校验通过，1 表示操作错误。

测试脚本覆盖重试次数、提前成功、取消、清单校验和界面渲染；`-Online` 另外验证真实下载、缓存复用、损坏文件检测与修复、错误 PDB 标识拒绝及失败清单重试。测试输出写入 `build`，不包含在仓库中。离线目标电脑的实际权限、驱动枚举和调试会话需要现场验证。

项目只处理 Windows 公共符号；微软未公开或暂不可用的符号会按规则重试并报告，不保证所有 Windows 模块都能下载。第三方组件保留原有声明，不因本项目而变更其许可。

## 项目结构

```text
symbol/
  src/SymbolTool.cs                 # 应用源码
  tests/Validate.ps1                # 验证脚本
  vendor/windows-debug-tools.zip    # 编译时嵌入 EXE 的依赖
  vendor/NOTICE.txt                 # 依赖包随附声明
  dist/SymbolTool.exe               # 编译好的 Windows x64 程序
  dist/SHA256SUMS.txt               # EXE 校验值
  dist/THIRD-PARTY-NOTICE.txt        # 随程序分发的第三方声明
  SymbolTool.csproj                 # Visual Studio / MSBuild 项目
  build.ps1                        # 无需额外安装 SDK 的构建入口
  README.md
  .gitignore
  .gitattributes
```

源码和正式 EXE 均可提交。`vendor` 是离线重新构建所需的输入，请保留。`build`、`.symbol-runtime`、导出清单目录和下载的符号目录由程序生成，已通过 `.gitignore` 排除。

参考：
- https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/using-a-manifest-file-with-symchk
- https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/microsoft-public-symbols
- https://learn.microsoft.com/en-us/windows/win32/api/dbghelp/nf-dbghelp-symsrvgetfileindexinfow
