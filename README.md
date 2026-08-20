# 番茄时钟

Windows 11 x64 本地番茄时钟，C# / .NET 8 / WPF / SQLite。

## 构建

```powershell
$env:DOTNET_CLI_HOME="$pwd\.dotnet"
$env:NUGET_PACKAGES="$pwd\.nuget\packages"
dotnet restore PomodoroClock.slnx --configfile NuGet.config
dotnet build PomodoroClock.slnx -c Debug --no-restore
dotnet test PomodoroClock.slnx -c Release --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File tests/StartupSmokeTest.ps1
dotnet publish src/PomodoroClock.App/PomodoroClock.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts/publish/win-x64
```

测试项目使用本机可用的 .NET 10 testhost，测试的核心和数据程序集仍为 .NET 8；发布应用是 self-contained win-x64，无需用户安装 .NET。

## 安装包

安装 Inno Setup 后执行：

```powershell
ISCC.exe installer\PomodoroClock.iss
```

输出：`artifacts/installer/PomodoroClock-Setup.exe`。安装器默认保留 `%LocalAppData%\番茄时钟` 数据，卸载结束时可明确选择删除。

## 数据

数据库：`%LocalAppData%\番茄时钟\Data\pomodoro.db`；日志：`%LocalAppData%\番茄时钟\Logs`。
