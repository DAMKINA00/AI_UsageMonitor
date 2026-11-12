using System.Windows;
using System.Windows.Controls;

namespace AIUsageMonitor.UI;

public partial class AccountsPanel : UserControl
{
    public event Action? AddClaudeRequested;
    public event Action? AddCodexRequested;
    public event Action? RefreshRequested;
    public event Action? CloseRequested;

    public AccountsPanel()
    {
        InitializeComponent();
    }

    public void Update(IReadOnlyList<AccountUsageCard> accounts, bool loginBusy)
    {
        if (!ReferenceEquals(AccountItems.ItemsSource, accounts))
            AccountItems.ItemsSource = accounts;

        AddClaudeButton.IsEnabled = !loginBusy;
        AddCodexButton.IsEnabled = !loginBusy;
    }

    /// <summary>Hosted by ChatterBox: accounts are added, renamed and excluded there; this panel only shows them.</summary>
    public void SetManagedByChatterBox()
    {
        AccountsHint.Text = "계정은 ChatterBox에서 관리합니다 (추가·이름 변경·배정 제외·로그인). 이 목록은 ChatterBox의 계정 그대로입니다.";
        AddButtons.Visibility = Visibility.Collapsed;
    }

    void AddClaude_Click(object sender, RoutedEventArgs e) => AddClaudeRequested?.Invoke();

    void AddCodex_Click(object sender, RoutedEventArgs e) => AddCodexRequested?.Invoke();

    void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
    void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
