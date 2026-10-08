using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CtPrep.App.Services;
using CtPrep.App.ViewModels;

namespace CtPrep.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // 窗口标题不能用 {loc:Loc} 取词（Window.Title 不接受标记扩展返回的 Binding），
        // 只能在这里设，并在切换语言时跟着更新。
        ApplyLocalizedTitle();
        LocalizationService.Current.PropertyChanged += OnLanguageChanged;

        LogBox.TextChanged += (_, _) => LogBox.ScrollToEnd();
        Loaded += OnLoaded;
        Closed += (_, _) => LocalizationService.Current.PropertyChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => ApplyLocalizedTitle();

    private void ApplyLocalizedTitle() => Title = LocalizationService.Current.T("App.Title");

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // 启动后自动做一次系统检测，方便用户直接看到结论
        var refresh = _viewModel.RefreshCommand;
        if (refresh.CanExecute(null))
        {
            refresh.Execute(null);
        }

        await Task.CompletedTask;
    }
}
