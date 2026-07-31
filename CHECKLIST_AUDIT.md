# 开发清单验收审计

审计日期：2026-07-31  
依据：`docs/CSHARP_WPF_APP_DEVELOPMENT_CHECKLIST.md`

## 结论

仓库内可自动实现和验证的迁移工作已形成可发布版本，但整体清单尚不能宣告完成。真实
示波器、指定 PC、干净 Windows 机器和迁移观察期属于外部验收门槛，必须保留为待验收，
不得用模拟传输或单次启动冒烟代替。

| 范围 | 当前证据 | 状态 |
|---|---|---|
| M0 基准 | Python 127 通过/2 跳过；目标 CSV 性能工具；Python 黄金 JSON | 仓库内完成 |
| M1 工程 | 分层解决方案、锁文件、分析器、日志、配置、单实例 | 完成 |
| M2 CSV/绘图 | 流式读写、取消/进度、异常行号、抽稀、ScottPlot | 完成 |
| M3 波形交互 | 独立窗口、纯波形、游标、事件、书签、标注、参考波形、截图 | 自动功能完成；人工手感待验收 |
| M4 算法 | 统计、边沿、相位、RPM、Python 黄金比较 | 自动验证完成 |
| M5 仪器 | VISA、SCPI、RAW 分块、触发、通道、垂直设置、截图 | DSO-X 3024T 资源扫描、连接和 IDN 已实测；完整实机矩阵待验收 |
| M6 主窗口 | 连接、控制、抓波自动保存、最近文件、历史、进度与取消 | 完成 |
| M7 启动刹车 | 三种范围/制动、三态结果、定位、历史、截图归档 | 离线完成；工况待验收 |
| M8 验证服务 | ABZ 抖动、质量、方案、批量、基准、HTML/CSV 报告 | 完成 |
| M9 发布 | CI、漏洞扫描、自包含 ZIP、哈希、启动冒烟 | 本机完成；干净机待验收 |
| M10 切换 | 导入、备份、摘要、回退说明 | 工具完成；观察期与归档待验收 |

## Python 测试迁移映射

Python 收集到 129 个用例，其中当前执行结果为 127 通过、2 跳过。C# 当前执行 59 个
等价/重写后的测试。迁移按公开行为合并参数化和重复 UI 场景，不追求测试数量一比一。

| Python 测试文件 | 数量 | C# 等价验证 |
|---|---:|---|
| `tests/analysis/test_motor_jitter.py` | 8 | `MotorJitterAnalysisTests` |
| `tests/analysis/test_waveform_preparation.py` | 2 | `EnvelopeDecimatorTests` |
| `tests/device/test_transport.py` | 8 | `KeysightOscilloscopeTests` |
| `tests/infra/test_task_runner.py` | 2 | `BatchRunner`、后台取消和过期结果路径 |
| `tests/services/test_batch_and_quality.py` | 3 | `ValidationServicesTests`、`MotorJitterAnalysisTests` |
| `tests/services/test_comparison.py` | 1 | `BaselineComparisonTests` |
| `tests/services/test_profiles_and_reports.py` | 4 | `ValidationServicesTests` |
| `tests/services/test_waveform_workspace.py` | 3 | `WaveformWorkspaceStoreTests` |
| `tests/test_utils.py` | 74 | `WaveformAnalysisTests`、`StartupBrakeAnalysisTests`、Python 黄金比较 |
| `tests/ui/test_stop_jitter_dialog.py` | 5 | `AppSmokeTests` 与 ViewModel 三态/归档路径 |
| `tests/ui/test_waveform_rendering.py` | 16 | `AppSmokeTests`、`WaveformViewStateTests`、截图归档测试 |
| `tests/ui/test_waveform_theme.py` | 3 | WPF 资源和启动冒烟；视觉对比仍属人工验收 |

## 自动验收命令

```powershell
dotnet restore KeysightScopeApp.slnx --locked-mode
dotnet build KeysightScopeApp.slnx -c Release --no-restore
dotnet test KeysightScopeApp.slnx -c Release --no-build --no-restore
dotnet format KeysightScopeApp.slnx --verify-no-changes --no-restore
dotnet list KeysightScopeApp.slnx package --vulnerable --include-transitive
..\.venv\Scripts\python.exe -m pytest -q
dotnet run --project tools/KeysightScopeApp.Benchmark -c Release -- <waveform.csv>
```

## 不能由当前仓库环境关闭的验收门槛

- 每个目标 Keysight/Agilent 型号、固件、USB VISA 和 LAN VISA 的实机矩阵。
- 设备拔线、断电、真实超时、最大点数、多通道 RAW 深存储和长时间连续操作。
- 指定办公 PC 的连续平移/缩放主观手感、30 FPS、100 次窗口开关和内存快照。
- 真实启动刹车与 ABZ 停机抖动工况和报告人工签字。
- 干净 Windows、非管理员账号、中文路径、升级、回退和卸载验证。
- C# 版本观察期；观察期结束前不得移动或删除 Python 代码。
