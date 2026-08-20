using System.Threading;
using System.Windows;
using PomodoroClock.Infrastructure;
namespace PomodoroClock.App;
public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    protected override void OnStartup(StartupEventArgs e){_mutex=new Mutex(true,"PomodoroClock.SingleInstance",out var created);if(!created){Shutdown();return;}base.OnStartup(e);MainWindow=new MainWindow(new SqliteDatabase(),new LocalFileLogger());MainWindow.Show();}
    protected override void OnExit(ExitEventArgs e){_mutex?.Dispose();base.OnExit(e);}
}
