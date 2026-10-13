using System.Windows;

namespace AIUsageMonitor.UI;

public partial class AccountsWindow : Window
{
    public event Action? AddClaudeRequested;
    public event Action? AddCodexRequested;
    public event Action? RefreshRequested;

    public AccountsWindow()
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

    void AddClaude_Click(object sender, RoutedEventArgs e) => AddClaudeRequested?.Invoke();

    void AddCodex_Click(object sender, RoutedEventArgs e) => AddCodexRequested?.Invoke();

    void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
}
