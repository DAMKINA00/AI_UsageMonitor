using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using AIUsageMonitor.Core;

namespace AIUsageMonitor.UI;

public sealed class AccountUsageSlot : INotifyPropertyChanged
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

    StatusChip? _freshness;

    /// <summary>메인 슬롯이 따르는 카드 상태 칩. 조회 실패·오래된 값이면 서브 계정과 같이 흐리게 표시한다.</summary>
    public StatusChip? Freshness
    {
        get => _freshness;
        init
        {
            _freshness = value;
            if (value != null) value.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(StatusChip.IsDimmed)) return;
                foreach (var name in new[] { nameof(IsDimmed), nameof(UsageOpacity), nameof(WarningVisibility), nameof(ToolTipText) })
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            };
        }
    }

    /// <summary>사용량 경고 ("한도 도달(추정)" 등). 원 위 ⚠ 와 툴팁에 보인다 (흐리게 하지는 않는다).</summary>
    public string? Warning { get; init; }

    public bool IsDimmed => HasRefreshError || Freshness?.IsDimmed == true;
    public Visibility WarningVisibility => (IsDimmed || !string.IsNullOrEmpty(Warning)) && !IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public double UsageOpacity => IsDimmed ? 0.45 : 1;
    public double Remaining => Usage is { IsResetPending: false } usage ? 100 - usage.Value : 0;
    public string RemainingText => Usage == null || Usage.IsResetPending ? "—" : Fmt.Pct(Remaining);
    public string Label => Usage?.Label ?? "확인 중";
    /// <summary>원 아래 짧은 라벨 ("주간", "5시간").</summary>
    public string ShortLabel => Label.Replace(" 한도", "");
    string? _resetTimer;
    public string ResetTimer => _resetTimer ??= Usage?.ResetShort ?? "";
    public Brush LevelBrush => Usage?.LevelBrush ?? Palette.Muted;
    public Visibility ContentVisibility => IsEmpty ? Visibility.Hidden : Visibility.Visible;
    public Visibility RingVisibility => Usage == null ? Visibility.Hidden : Visibility.Visible;
    public string ToolTipText => string.Join('\n', new[]
        {
            Role, Account, Plan, Usage?.Label, Usage?.ResetTextAt(DateTimeOffset.Now), Warning != null ? "⚠ " + Warning : null, Status,
            Freshness is { AsOf: not null } f ? f.ToolTipText : null,
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string Signature => $"{Role}|{Id}|{Provider}|{Account}|{Plan}|{IsEmpty}|{Usage?.Label}|{Usage?.Value}|{Usage?.IsResetPending}|{Usage?.ResetsAt?.UtcTicks}|{Status}|{HasRefreshError}|{Warning}";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>남은 시간·흐림 상태를 지금 시각으로 다시 계산한다.</summary>
    public void Tick()
    {
        _resetTimer = Usage?.ResetShortAt(DateTimeOffset.Now) ?? "";
        foreach (var name in new[] { nameof(ResetTimer), nameof(ToolTipText), nameof(IsDimmed), nameof(UsageOpacity), nameof(WarningVisibility) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <param name="secondarySlots">서브 계정 칸 수. 서브 계정을 지원하지 않는 서비스(Gemini)는 0.</param>
    public static List<AccountUsageSlot> Create(ProviderCard main, IReadOnlyList<SecondaryAccountState> accounts, int secondarySlots = 2)
    {
        var slots = new List<AccountUsageSlot>
        {
            new() { Role = "메인", Provider = main.Name, Account = main.Account, Plan = main.Plan, Usage = main.Usage.FirstOrDefault(),
                Freshness = main.Status, Warning = main.UsageWarning,
                Status = string.Join('\n', new[] { main.Notice ?? main.UsageEmpty ?? main.Footnote, main.Extras }
                    .Where(x => !string.IsNullOrWhiteSpace(x))) },
        };
        var secondary = accounts.Where(x => x.Settings.Provider.ToString() == main.Name).Take(secondarySlots).ToList();
        for (int index = 0; index < secondarySlots; index++)
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
