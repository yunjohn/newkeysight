# Keysight 示波器助手（C# / WPF）

这是现有 Python/PySide6 程序的并行迁移版本。Python 源码和用户数据不会被修改。

## 环境

- Windows x64
- .NET 10 SDK
- Keysight IO Libraries Suite（仅连接真实仪器时需要）

## 构建与运行

```powershell
cd dotnet
dotnet restore KeysightScopeApp.slnx
dotnet build KeysightScopeApp.slnx -c Release
dotnet test KeysightScopeApp.slnx -c Release
dotnet run --project src/KeysightScopeApp.App
```

应用在没有 VISA 或示波器时也能加载、分析、显示和导出 CSV。运行数据位于
`%LOCALAPPDATA%\KeysightScopeApp`，包含 `captures`、`reports`、`settings`、
`logs` 和 `profiles`。

设备抓波会自动保存到用户 `captures` 目录并加入最近波形列表。独立波形窗口支持 F11
纯波形模式、A/B 键放置游标、快速事件定位、测量冻结，以及时间轴和纵轴分别复位。
高级分析支持版本化测试方案、基准比较、可取消批量运行、三态历史及包含标准截图的完整
归档。

PNG 导出可选择完整窗口、带游标/标注覆盖层的纯图或无覆盖层纯图。通道相位比较完成后
可单独导出诊断 CSV。常用快捷键为 Ctrl+O（加载）、Ctrl+S（导出）、Ctrl+W（波形
窗口）、F5（刷新 VISA）和 Esc（取消）。

## 当前硬件接入边界

`IScopeTransport` 和 `IVisaSessionFactory` 将设备 API 与业务逻辑隔离。脚本化传输覆盖
SCPI、截图、波形缩放和 RAW 分块测试；真实 VISA 会话使用 Keysight VISA .NET API，
需要安装 Keysight IO Libraries Suite，并在目标机完成型号/固件兼容性验收。波形读取使用
Keysight/Agilent InfiniiVision 常见的
`:WAVeform:*` 命令。

## 性能基准

```powershell
dotnet run --project tools/KeysightScopeApp.Benchmark -c Release -- <waveform.csv>
```

工具输出 CSV 解析、1920 像素包络抽稀、全量统计、显示点数和托管内存增量。

## 从 Python 版导入数据

可以在主窗口选择“导入 Python 数据”，也可以运行：

```powershell
dotnet run --project tools/KeysightScopeApp.Migrate -c Release -- <Python项目目录>
```

导入工具会先备份识别到的 Python 配置和历史文件，再写入 C# 用户数据目录；不会修改
Python 源文件或原配置。重复导入会生成新的摘要，失败后可以安全重试。

## 发布

```powershell
dotnet publish src/KeysightScopeApp.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:PublishReadyToRun=true
```
