using System.Windows;
using AIUsageMonitor.Core;

namespace AIUsageMonitor.UI;

/// <summary>
/// 'agy 버전 미지원' 차단을 사용자가 푸는 동작. 확인을 받은 뒤에만 Core 의 ClearGeminiBlock 을 부른다
/// (그 조회가 다시 미지원이면 Core 가 다시 막는다).
/// </summary>
public static class GeminiBlockAction
{
    public const string MenuText = "Gemini 조회 차단 해제";
    public const string ConfirmTitle = "Gemini 조회 차단 해제";
    public const string ConfirmText =
        "agy 가 /usage 를 일반 프롬프트로 처리하는 버전이면 이번 조회에서 쿼터가 1회 쓰일 수 있습니다. 계속할까요?";

    /// <summary>
    /// 확인 창을 띄운다 (기본 단추는 '아니요'). 메인 창이 보이면 그 창을 owner 로 삼아 항상 위 메인 창 뒤에 숨지 않게 하고,
    /// 메인 창이 숨겨져 있으면 화면 가운데의 보이지 않는 항상 위 임시 창을 owner 로 쓴다.
    /// </summary>
    public static bool Confirm(Window? main, string text)
    {
        Window? temporary = null;
        var owner = main is { IsVisible: true, WindowState: not WindowState.Minimized } ? main : null;
        if (owner == null)
        {
            var area = SystemParameters.WorkArea;
            temporary = new Window
            {
                Width = 1, Height = 1, Left = area.Left + area.Width / 2, Top = area.Top + area.Height / 2,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                Topmost = true, ShowActivated = true, Background = System.Windows.Media.Brushes.Transparent,
            };
            temporary.Show();
            temporary.Activate();
            owner = temporary;
        }
        try
        {
            return MessageBox.Show(owner, text, ConfirmTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes;
        }
        finally { temporary?.Close(); }
    }

    /// <summary>차단돼 있을 때만 확인을 묻고, 사용자가 동의하면 차단을 풀고 한 번 조회한다. 실제로 풀었으면 true.</summary>
    public static async Task<bool> RunAsync(MonitorService monitor, Func<string, bool> confirm)
    {
        if (!monitor.GeminiUnsupported) return false;
        if (!confirm(ConfirmText)) return false;
        await monitor.ClearGeminiBlock();
        return true;
    }
}
