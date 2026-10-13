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

    public static List<AccountUsageCard> Create(ProviderCard claude, ProviderCard codex, SecondaryAccountService service)
    {
        var cards = new List<AccountUsageCard>();
        foreach (var main in new[] { claude, codex })
        {
            cards.Add(new AccountUsageCard
            {
                Id = "primary:" + main.Name, ProviderName = main.Name + " · 주 계정",
                DisplayName = Fmt.AccountId(main.Account) ?? "계정 확인 중", Plan = main.Plan,
                Status = main.Notice ?? main.UsageEmpty ?? main.Footnote,
                Usage = main.Usage,
            });
            foreach (var account in service.Accounts.Where(x => x.Settings.Provider.ToString() == main.Name))
            {
                var id = account.Settings.Id;
                cards.Add(new AccountUsageCard
                {
                    Id = id, ProviderName = main.Name + " · 서브 계정",
                    DisplayName = Fmt.AccountId(account.AccountName) ?? "새 계정", Plan = account.Usage?.Plan,
                    Status = account.Status, Usage = Presenter.DisplayUsage(account.Usage?.Windows ?? []),
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
