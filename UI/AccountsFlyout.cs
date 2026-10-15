using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AIUsageMonitor.UI;

/// <summary>독립 HWND를 사용해 계정 패널을 열어도 메인 창의 위치와 레이아웃은 바꾸지 않는다.</summary>
public sealed class AccountsFlyout : Window
{
    public event Action? CloseRequested;

    public AccountsFlyout(AccountsPanel panel)
    {
        Title = "계정 사용량";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        UseLayoutRounding = true;
        Background = new SolidColorBrush(Color.FromRgb(27, 28, 33));
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(51, 54, 62)),
            BorderThickness = new Thickness(1),
            Child = panel,
        };
    }

    public void PositionBeside(Window owner, Rect area)
    {
        const double margin = 12, gap = 8;
        Width = Math.Min(400, Math.Max(1, area.Width - margin * 2));
        // 메인 창이 작아졌으므로 높이는 메인 창이 아니라 작업 영역에 맞춘다 (계정이 많으면 패널 안에서 스크롤)
        Height = Math.Min(900, Math.Max(1, area.Height - margin * 2));
        double leftSpace = owner.Left - area.Left - margin;
        double rightSpace = area.Right - margin - owner.Left - owner.Width;
        bool onLeft = leftSpace >= Width + gap || leftSpace >= rightSpace;
        double left = onLeft ? owner.Left - Width - gap : owner.Left + owner.Width + gap;
        // 공간이 부족할 때는 플로팅 패널만 화면 안으로 이동한다.
        Left = Math.Clamp(left, area.Left + margin, Math.Max(area.Left + margin, area.Right - Width - margin));
        Top = Math.Clamp(owner.Top, area.Top + margin, Math.Max(area.Top + margin, area.Bottom - Height - margin));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseRequested?.Invoke();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4는 패널만 숨기고, 소유 창이 종료될 때는 WPF가 함께 정리한다.
        if (Owner is { IsVisible: true })
        {
            e.Cancel = true;
            CloseRequested?.Invoke();
        }
        base.OnClosing(e);
    }
}
