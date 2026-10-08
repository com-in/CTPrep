using System.Windows;
using System.Windows.Threading;
using CtPrep.App.Services;
using CtPrep.App.ViewModels;
using CtPrep.App.Views;

namespace CtPrep.App;

public partial class App : Application
{
    private AppLogger? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 配置文件还没加载，先按系统语言显示后续提示
        var loc = LocalizationService.Current;
        loc.SetLanguage(null);

        // 部署会压缩和格式化系统分区；程序本身位于 C: 时，运行文件可能会被占用，
        // 从而导致分区操作失败或留下不完整的部署环境。
        if (IsRunningOnCDrive())
        {
            MessageBox.Show(
                loc.T("Msg.RunOnCDriveBody", AppContext.BaseDirectory),
                loc.T("Msg.RunOnCDriveTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(0);
            return;
        }

        _logger = new AppLogger();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger?.Error($"未处理的异常：{args.ExceptionObject}");

        try
        {
            var configService = new ConfigService(_logger);
            var config = configService.Load();
            var customConfig = configService.LoadCustom();
            _logger.MinLevel = config.LogLevel;
            _logger.AttachFile(Path.Combine(config.RuntimeDir, "logs"));

            // 语言优先级：runtime\ui-language.txt（界面上改过的） > 配置里的 Language > 系统语言
            loc.SetLanguage(LocalizationService.ReadSaved(config.RuntimeDir) ?? config.Language);

            var viewModel = new MainViewModel(config, customConfig, _logger);
            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            // 日志里保留完整调用栈；弹窗里给出「消息 + 逐层内部异常」，
            // 否则像 XamlParseException 这类外层只有一句话的异常会掩盖真正原因。
            _logger.Error($"启动失败：{ex}");
            MessageBox.Show(
                loc.T("Msg.StartupFailedBody", Describe(ex), AppContext.BaseDirectory),
                loc.T("Msg.StartupFailedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error($"未处理的界面异常：{e.Exception}");
        MessageBox.Show(
            Describe(e.Exception),
            LocalizationService.Current.T("Msg.UnexpectedTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>把异常写成「消息 + 逐层内部异常」，方便一眼看出根因。</summary>
    private static string Describe(Exception ex)
    {
        var text = new StringBuilder(ex.Message);
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            text.AppendLine();
            text.Append("→ ").Append(inner.Message);
        }

        return text.ToString();
    }


    private static bool IsRunningOnCDrive()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(AppContext.BaseDirectory));
        return string.Equals(root, @"C:\", StringComparison.OrdinalIgnoreCase);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Close();
        base.OnExit(e);
    }
}
