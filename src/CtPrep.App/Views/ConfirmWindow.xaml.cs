using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using CtPrep.App.Services;

namespace CtPrep.App.Views;

/// <summary>确认弹窗的一行文案：<paramref name="Critical"/> 为 true 时以红色加粗显示。</summary>
public sealed record ConfirmLine(string Text, bool Critical = false);

/// <summary>
/// 带强制阅读时间的确认窗口：
/// 确定按钮要等 <see cref="ConfirmWindow.RequiredDelaySeconds"/> 秒之后才可点击，
/// 防止用户不看内容直接回车；标记为危险的行一律红色显示。
/// </summary>
public partial class ConfirmWindow : Window
{
    /// <summary>确定按钮的强制等待秒数。</summary>
    public const int RequiredDelaySeconds = 3;

    private readonly DispatcherTimer _timer;
    private int _remaining;

    public ConfirmWindow(string title, IEnumerable<ConfirmLine> lines, bool critical = false)
    {
        InitializeComponent();

        Title = title;
        TitleText.Text = title;

        var hasCritical = false;
        var first = true;
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line.Text))
            {
                continue;
            }

            if (!first)
            {
                MessageText.Inlines.Add(new LineBreak());
            }

            first = false;

            var run = new Run(line.Text);
            if (line.Critical)
            {
                hasCritical = true;
                run.Foreground = (Brush)FindResource("DangerBrush");
                run.FontWeight = FontWeights.SemiBold;
            }

            MessageText.Inlines.Add(run);
        }

        // 危险内容：整卡片换成红底红边，确定按钮也用红色
        if (hasCritical || critical)
        {
            BodyBorder.Background = (Brush)FindResource("DangerSoftBackground");
            BodyBorder.BorderBrush = (Brush)FindResource("DangerBrush");
            OkButton.Style = (Style)FindResource("DangerButton");
        }

        _remaining = RequiredDelaySeconds;
        OkButton.Content = CountdownText();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>用户点了确定返回 true，取消或关闭返回 false。</summary>
    public bool Confirmed { get; private set; }

    private string CountdownText() =>
        LocalizationService.Current.T("Dialog.ConfirmWait", _remaining);

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining > 0)
        {
            OkButton.Content = CountdownText();
            return;
        }

        _timer.Stop();
        OkButton.IsEnabled = true;
        OkButton.Content = LocalizationService.Current.T("Dialog.ConfirmOk");
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!OkButton.IsEnabled)
        {
            return;
        }

        Confirmed = true;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
        Close();
    }
}
