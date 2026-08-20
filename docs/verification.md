# 构建与验收记录

## 已执行

- Debug 构建：`dotnet build PomodoroClock.slnx -c Debug --no-restore`（通过）
- Release 自动化测试：`dotnet test PomodoroClock.slnx -c Release --no-restore`（7 通过）
- NuGet 恢复：通过工作区 `NuGet.config` 完成
- Inno Setup 7.1.0 x64：已安装并成功编译 `artifacts/installer/PomodoroClock-Setup.exe`
- UI 美化：根据 `design-system/pomodoroclock/MASTER.md` 应用暖白/番茄红设计令牌、卡片层级、统一按钮状态、侧栏导航和键盘可见焦点风格。

## 自动化覆盖

计时完成、提前结束、重置、无暂停状态、长休息周期、设置范围、SQLite 持久化、事务关联、重复关联防护。

## 手工验收清单

首次运行、修改设置、开始/结束专注、结算时多选事项和新建事项、统计页刷新、关闭窗口到托盘、托盘恢复/退出、第二实例激活、重启后数据库保留、100%/125%/150% 缩放。

## 环境限制

当前机器未安装 .NET 8 Desktop Runtime 和 Inno Setup。应用发布配置为 self-contained，可在安装包目标机运行；安装器脚本已完成但需 `ISCC.exe` 才能生成最终 `.exe`。
