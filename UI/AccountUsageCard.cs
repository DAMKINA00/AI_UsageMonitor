using System.Windows.Input;
using AIUsageMonitor.Core;

namespace AIUsageMonitor.UI;

public sealed class AccountUsageCard
{
    public required string Id { get; init; }
    public required string ProviderName { get; init; }
    public required string DisplayName { get; init; }
    public string? Plan { get; init; }
    public required string Status { get; init; }
    public List<UsageRow> Usage { get; init; } = [];
    public bool IsSecondary { get; init; }
    public bool CanLogin { get; init; }
    public string LoginText { get; init; } = "브라우저 로그인";
    public ICommand? LoginCommand { get; init; }
    public ICommand? RemoveCommand { get; init; }

    public string Signature => string.Join('|', Id, ProviderName, DisplayName, Plan, Status, CanLogin, LoginText,
        string.Join(';', Usage.Select(x => $"{x.Label}|{x.Value}|{x.ResetText}")));

    /// <param name="gemini">있으면 주 계정 카드만 덧붙인다 (Gemini 는 서브 계정을 지원하지 않는다).</param>
    public static List<AccountUsageCard> Create(ProviderCard claude, ProviderCard codex, SecondaryAccountService service, ProviderCard? gemini = null, ProviderCard? grok = null)
    {
        var cards = new List<AccountUsageCard>();
        foreach (var main in new[] { claude, codex, gemini, grok }.OfType<ProviderCard>())
        {
            cards.Add(new AccountUsageCard
            {
                Id = "primary:" + main.Name, ProviderName = main.Name + " · 주 계정",
                DisplayName = Fmt.AccountId(main.Account) ?? (main.Name == "Gemini" ? "ANTIGRAVITY CLI" : "계정 확인 중"), Plan = main.Plan,
                Status = main.Notice ?? main.UsageEmpty ?? main.Footnote,
                Usage = main.AccountUsage.Count > 0 ? main.AccountUsage : Presenter.AccountUsageRows(main.RawWindows),
            });
            foreach (var account in service.Accounts.Where(x => x.Settings.Provider.ToString() == main.Name))
            {
                var id = account.Settings.Id;
                cards.Add(new AccountUsageCard
                {
                    Id = id, ProviderName = main.Name + " · 서브 계정",
                    DisplayName = Fmt.AccountId(account.AccountName) ?? "새 계정", Plan = account.Usage?.Plan,
                    Status = account.Status, Usage = Presenter.AccountUsageRows(account.Usage?.Windows ?? [], account.Usage?.FiveHourUnlimited == true),
                    IsSecondary = true, CanLogin = !service.LoginBusy && !account.Removed,
                    LoginText = account.IsLoggingIn ? "로그인 진행 중…" : account.Usage == null ? "브라우저 로그인" : "다시 로그인",
                    LoginCommand = new RelayCommand(async () => await service.LoginAsync(id)),
                    RemoveCommand = new RelayCommand(async () => await service.RemoveAsync(id)),
                });
            }
        }
        return cards;
    }

}
