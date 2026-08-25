using System.IO;
using System.Text;
using System.Windows;
using HdmiSwitch.Services;

namespace HdmiSwitch;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => string.Equals(a, "--probe", StringComparison.OrdinalIgnoreCase)))
        {
            var snapshot = MonitorHub.Capture();
            var sb = new StringBuilder();
            sb.AppendLine($"hdmiPorts={snapshot.HdmiPortCount} hdmiWithSink={snapshot.HdmiWithSinkCount} outputs={snapshot.Outputs.Count}");
            foreach (var item in snapshot.Outputs)
            {
                sb.AppendLine($"{item.Title} | {item.Connector} | {item.SignalText} | {item.PathText} | 輸入={item.CurrentInputText} | DDC={item.DdcText}");
            }

            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "probe-result.txt"), sb.ToString());
            Shutdown();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}