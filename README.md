# ClipImageToPath

Windows 常驻托盘程序：检测到剪贴板含图片时，自动将图片保存为 `%TEMP%` 下的临时 PNG 文件，并把剪贴板内容替换为该文件的完整路径，方便直接粘贴出路径。

## 目录结构

```
ClipImageToPath/
├── src/          源码（csproj + C# 文件）
│   └── Assets/   程序图标（app.ico）
├── scripts/      构建辅助脚本
│   ├── check-env.ps1   环境检查
│   ├── build.ps1       编译
│   ├── generate-icon.ps1 重新生成程序图标
│   └── publish.ps1     打包发布单文件 exe
├── artifacts/    发布产物（publish/ 下为 exe）
├── build/        构建中间产物（bin/obj）
└── README.md     本文件
```

## 脚本用法

```powershell
scripts\check-env.ps1              # 环境检查（可加 -CheckNetwork 验证 NuGet 源）
scripts\build.ps1                  # Release 编译
scripts\build.ps1 -Clean           # 清理后重新编译
scripts\generate-icon.ps1          # 重新生成 src\Assets\app.ico
scripts\publish.ps1                # 发布单文件 exe 到 artifacts\publish
scripts\publish.ps1 -Version 1.1.0 # 指定版本发布（默认取 csproj 的 <Version>）
```

## 版本管理

- 版本号唯一来源是 [src/ClipImageToPath.csproj](src/ClipImageToPath.csproj) 中的 `<Version>`（当前 `1.0.5`）。
- 发布时 `publish.ps1` 默认读取该版本号，也可用 `-Version` 覆盖。
- 版本号同步生效三处：exe 文件名（`ClipImageToPath_1.0.5.exe`）、exe 文件属性（右键 → 详细信息）、程序内信息（命令行 `--version` 输出）。

## 使用

双击 `artifacts\publish\ClipImageToPath_1.0.5.exe` 即常驻托盘。复制任意图片后，剪贴板会被替换为临时图片文件的完整路径，粘贴即得路径。托盘右键菜单包含：
- **图片转路径**（默认勾选）：功能总开关，取消勾选后程序仍常驻托盘但不再把剪贴板图片转为路径，状态持久化到注册表。
- **开机自启**（默认勾选）：写入当前用户注册表 Run 键（记录实际运行路径，文件名带版本不影响）。
- **消息提示**（默认勾选）：控制图片转换成功后的气泡提示。
- **退出**：结束程序。

命令行用法：

```powershell
ClipImageToPath_1.0.5.exe --version   # 输出版本号
ClipImageToPath_1.0.5.exe --selftest  # 端到端自检，退出码 0 为通过
```

日志位置：`%TEMP%\ClipImageToPath\clip.log`。
