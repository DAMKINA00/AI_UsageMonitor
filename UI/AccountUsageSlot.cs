using System.Windows;
using System.Windows.Media;
using AIUsageMonitor.Core;

namespace AIUsageMonitor.UI;

public sealed class AccountUsageSlot
{
    public required string Role { get; init; }
    public string Id { get; init; } = "";
    public string Provider { get; init; } = "";
    public bool CanDrag => !IsEmpty && Id.Length > 0;
    public bool CanSwapWith(AccountUsageSlot? other) => CanDrag && other?.CanDrag == true &&
        Id != other.Id && Provider == other.Provider;
    public string? Account { get; init; }
    public UsageRow? Usage { get; init; }
    public string Status { get; init; } = "";
    public string? Plan { get; init; }
    public bool IsEmpty { get; init; }
    public bool HasRefreshError { get; init; }
    public Visibility WarningVisibility => HasRefreshError && !IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public double UsageOpacity => HasRefreshError ? 0.45 : 1;
    public double Remaining => Usage is { IsResetPending: false } usage ? 100 - usage.Value : 0;
    public string RemainingText => Usage == null || Usage.IsResetPending ? "—" : Fmt.Pct(Remaining);
    public string Label => Usage?.Label ?? "확인 중";
    public string ResetTimer => Usage?.ResetShort ?? "";
    public Brush LevelBrush => Usage?.LevelBrush ?? Palette.Muted;
    public Visibility ContentVisibility => IsEmpty ? Visibility.Hidden : Visibility.Visible;
    public Visibility RingVisibility => Usage == null ? Visibility.Hidden : Visibility.Visible;
    public string ToolTipText => string.Join('\n', new[] { Role, Account, Plan, Usage?.Label, Usage?.ResetText, Status }
        .Where(x => !string.IsNullOrWhiteSpace(x)));
    public string Signature => $"{Role}|{Id}|{Provider}|{Account}|{Plan}|{IsEmpty}|{Usage?.Label}|{Usage?.Value}|{Usage?.ResetText}|{ResetTimer}|{Status}|{HasRefreshError}";

    public static List<AccountUsageSlot> Create(ProviderCard main, IReadOnlyList<SecondaryAccountState> accounts)
    {
        var slots = new List<AccountUsageSlot>
        {
            new() { Role = "메인", Provider = main.Name, Account = main.Account, Plan = main.Plan, Usage = main.Usage.FirstOrDefault(),
                Status = string.Join('\n', new[] { main.Notice ?? main.UsageEmpty ?? main.Footnote, main.Extras }
                    .Where(x => !string.IsNullOrWhiteSpace(x))) },
        };
        var secondary = accounts.Where(x => x.Settings.Provider.ToString() == main.Name).Take(2).ToList();
        for (int index = 0; index < 2; index++)
        {
            var account = secondary.ElementAtOrDefault(index);
            slots.Add(new AccountUsageSlot
            {
                Role = $"서브{index + 1}", Account = Fmt.AccountId(account?.AccountName), Plan = account?.Usage?.Plan,
                Id = account?.Settings.Id ?? "", Provider = main.Name,
                Usage = Presenter.DisplayUsage(account?.Usage?.Windows ?? []).FirstOrDefault(),
                IsEmpty = account?.Usage == null && account?.AccountName == null,
                HasRefreshError = account?.HasRefreshError == true,
                Status = account?.Status ?? "계정 메뉴에서 추가할 수 있습니다.",
            });
        }
        return slots;
    }
}
