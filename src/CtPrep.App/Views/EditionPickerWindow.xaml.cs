using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using CtPrep.App.Models;
using CtPrep.App.Services;

namespace CtPrep.App.Views;

/// <summary>多版本映像的手工选择窗口；单版本映像不会走到这里。</summary>
public partial class EditionPickerWindow : Window
{
    public EditionPickerWindow(IReadOnlyList<ImageInfo> editions, int preselectIndex)
    {
        InitializeComponent();

        // 窗口标题在代码里设：Window.Title 不接受 {loc:Loc} 返回的 Binding
        Title = LocalizationService.Current.T("Edition.Title");
        EditionList.ItemsSource = editions;

        var start = editions.ToList().FindIndex(e => e.Index == preselectIndex);
        EditionList.SelectedIndex = start >= 0 ? start : 0;
        EditionList.Focus();
    }

    /// <summary>用户选中的版本；未选中为 null。</summary>
    public ImageInfo? SelectedEdition => EditionList.SelectedItem as ImageInfo;

    private void OnConfirm(object sender, RoutedEventArgs e) => Confirm();

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => Confirm();

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Confirm();
        }
    }

    private void Confirm()
    {
        if (SelectedEdition is null)
        {
            return;
        }

        DialogResult = true;
        Close();
    }
}
