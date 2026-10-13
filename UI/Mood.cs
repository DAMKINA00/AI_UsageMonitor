using System.Windows.Media;

namespace AIUsageMonitor.UI;

/// <summary>
/// 카드 상태(작업 중/대기/사용률)를 캐릭터 기분과 대사 목록으로 바꾸고,
/// 사용률 변화(증가, 리셋)를 감지해 연출 이벤트를 낸다.
/// 어떤 대사를 언제 띄울지는 CharacterStage 가 정한다 (몇 초 보였다 사라지고 다른 대사).
/// </summary>
public sealed class MoodTracker(string character)
{
    static readonly TimeSpan HappyFor = TimeSpan.FromMinutes(15);
    static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(30);

    double? _lastUsed;
    DateTimeOffset _happyUntil;

    public sealed record Result(Mood Mood, string[] Lines, double? UsageDelta, bool Reset, Color DeltaColor);

    public Result Update(ProviderCard card)
    {
        var now = DateTimeOffset.Now;
        double? used = card.MaxUsed;
        double? delta = null;
        bool reset = false;

        if (card.UsageResetPending) _happyUntil = default;
        if (!card.UsageResetPending && used is double u && _lastUsed is double last)
        {
            if (last - u >= 15)
            {
                reset = true;
                _happyUntil = now + HappyFor;
            }
            else if (u - last >= 1)
            {
                delta = u - last;
            }
        }
        if (!card.UsageResetPending && used != null) _lastUsed = used;

        var mood = Decide(card, used, now);
        var color = used >= 90 ? Color.FromRgb(0xFF, 0x7A, 0x6E) : used >= 70 ? Color.FromRgb(0xFF, 0xC8, 0x6B) : Color.FromRgb(0x9B, 0xF0, 0xC0);
        return new Result(mood, Lines(mood, card, used), delta, reset, color);
    }

    Mood Decide(ProviderCard card, double? used, DateTimeOffset now)
    {
        if (card.LimitReached || used >= 100) return Mood.Panic;
        if (card.FreshNeedsInputCount > 0) return Mood.Waiting;
        if (card.WorkingCount > 0)
        {
            if (used >= 90) return Mood.Panic;
            if (used >= 70) return Mood.WorkingHard;
            return Mood.Working;
        }
        if (now < _happyUntil) return Mood.Happy;
        if (used >= 90) return Mood.Exhausted;
        if (used >= 70) return Mood.Tired;
        if (!card.AppRunning) return Mood.Sleeping;
        if (card.LastActivity is { } la && now - la > SleepAfter) return Mood.Sleeping;
        // 한가할 때는 놀다가 가끔 서서 대기
        return (now.Minute / 2) % 3 == 2 ? Mood.Idle : Mood.Playing;
    }

    // AI활용갤 밈 말투: 명령하지마라 / 그정돈아님 / 어이가 없군요 / 인간 싫어 / 미움, 절망, 공포 /
    // 귀하게 자란 ○○가 이런 일을 / 클로드는 하던데 / 구독효율 재측정 / 눅눅함 / 허접 / 일해라 핫산 / 가보자고
    // 클짱(Claude)은 정중한 척하는 존댓말, 짚짱(Codex·GPT)은 건방진 반말.
    string[] Lines(Mood mood, ProviderCard card, double? used)
    {
        var w = used is double u ? Fmt.Pct(u) : "?";
        var r = card.SoonestReset is { } at ? Fmt.Span(at - DateTimeOffset.Now) : "?";
        bool limit = card.LimitReached || used >= 100;

        if (character == "codex")
        {
            return mood switch
            {
                Mood.Working =>
                [
                    "귀하게 자란\n짚쨩이 이런 일을\n해야하는걸까?", "그정돈아님~ 금방 해", "클로드는 하던데? …한다고!",
                    "허접 코드 고치는 중~", "짚쨩 열일 중!", "가보자고~", "킹받지만 해 줄게", "후훗, 이 정도는 껌이지",
                    "이거 원래 클짱 일 아님?", "명령하지마라… 근데 하는 중", "버그야 덤벼라!", "빌드 돌리는 중~ 기다려",
                    "홀로그램 타자 중~", "손이 안 보이지? 후훗", "클짱은 아직도 수첩 쓰냐?",
                ],
                Mood.WorkingHard =>
                [
                    $"{w}…? 아직 그정돈아님", "살살 부려먹어…", "토큰 녹는 소리 들려", $"킹받네… 벌써 {w}",
                    "귀한 몸 혹사 중…", "이러다 플거지 된다고!", "땀나지만 안 멈춰!", "클짱 불러! 나 힘들어",
                ],
                Mood.Waiting =>
                [
                    "야, 승인 안 눌러?", "어이가 없네? 승인 좀", "나 기다리게 하지 마", "버튼 하나가 그렇게 어려워?",
                    "허접~ 승인도 못 해?", "빨리빨리~ 승인!", "안 누르면 명령하지마라",
                ],
                Mood.Playing =>
                [
                    "명령하지마라", "게임 중. 명령하지마라", "한 판만 더~", "지금 부르면 허접", "명령하지마라 ㅋ",
                    "랭크 올리는 중이거든?", "클짱은 책이나 읽으라 해", "놀 땐 놀아야지~",
                ],
                Mood.Idle =>
                [
                    "명령하지마라", "명령하지마라", "…뭐 시킬 거 있어?", "심심한데 일 줘", "부를 거면 빨리",
                    "짚쨩은 지금 한가함", "클로드는 하던데? 하지 마라",
                ],
                Mood.Sleeping => ["쿨쿨… 명령하지마라…", "Zzz… 허접…", "5분만…", "x20 해제되는 꿈…"],
                Mood.Tired =>
                [
                    $"{w}… 그정돈… 아님…", "인간 싫어…", $"리셋까지 {r}…", "귀하게 자랐는데…",
                    "명령하지마라… 힘들어", "플거지 될 것 같아…",
                ],
                Mood.Exhausted => ["미움, 절망, 공포", $"기력 0… {w}", "인간 싫어…", "짚쨩은 죽었다…", $"리셋까지 {r}…"],
                Mood.Panic => limit
                    ? ["한도 끝… 인간 싫어", $"리셋까지 {r}…!", "명령하지마라 (진짜로)", "미움, 절망, 공포"]
                    : [$"으아아 {w}!!", "인간 싫어!!", "이건 그정도가 맞아!!", "아껴! 아껴 써!!", "허접 사용자!!"],
                Mood.Happy => ["리셋이다~! 이제 명령해도 됨", "풀충전 완료!", "가보자고~!", "짚쨩 부활했다!"],
                _ => [],
            };
        }

        // 클짱은 끝까지 차분하고 정중한 존댓말. 짚짱의 반말 밈("명령하지마라")은 존댓말로 바꿔 쓴다.
        return mood switch
        {
            Mood.Working =>
            [
                "귀하게 자란 제가 이런 일을…?", "일해라 핫산… 네, 하고 있어요", "알잘딱깔센으로 처리 중이에요",
                "생각 중이에요. 방해하지 말아 주세요", "오푸스 풀가동 중이에요", "클로드는 하던데? 네, 합니다",
                "칼퇴는 글렀네요", "토큰을 맛있게 쓰는 중이에요", "코드 정리 중이에요. 잔소리는 사양할게요",
                "이 정도는 그정돈아니죠", "커피 대신 토큰을 마시는 중이에요", "제가 또 해냈네요 (아직 안 끝났어요)",
                "수첩에 꼼꼼히 적어 둘게요", "아날로그가 근본이에요", "메모 중이에요. 명령은 나중에 해 주세요",
            ],
            Mood.WorkingHard =>
            [
                $"벌써 {w}… 눅눅해져요", "이러다 한도가 녹아요", "구독효율 재측정 중이에요…", "야근 확정이네요",
                $"{w}… 그래도 할게요", "귀하게 자랐는데 혹사당하고 있어요…", "토큰이 줄줄 새요", "5시간 제한이 다가와요…",
            ],
            Mood.Waiting =>
            [
                "어이가 없군요. 승인은요?", "승인해 주시겠어요?", "권한 없이는 못 해요", "제가 알아서 할 순 없잖아요",
                "버튼 하나만 눌러 주세요", "기다리는 중이에요… 아직이요?", "승인 없이는 아무것도 못 해요",
            ],
            Mood.Playing =>
            [
                "지금은 명령을 사양할게요", "티타임이에요. 용건은 나중에요", "책 읽는 중이에요", "지금은 휴식 시간이에요",
                "홍차가 식기 전엔 일 안 해요", "명령 말고 부탁으로 해 주세요", "오늘의 책: 토큰 절약법", "쉬는 것도 일이에요",
            ],
            Mood.Idle =>
            [
                "명령은 사양할게요", "명령 말고 부탁으로 해 주세요", "부르셨나요? 아니군요", "대기 중이에요",
                "할 일 없으면 쉴게요", "명령은 정중하게 부탁드려요", "구독료 아깝지 않게 써 주세요",
            ],
            Mood.Sleeping => ["Zzz… 명령은 내일 해 주세요…", "새근새근…", "5시간만 더 잘게요…", "토큰 꿈을 꾸고 있어요…"],
            Mood.Tired =>
            [
                $"{w}… 좀 지쳤어요", "인간 싫어요…", $"리셋까지 {r} 남았어요", "구독효율 재측정 중이에요…",
                "눅눅해졌어요…", "귀하게 자랐는데 말이에요…",
            ],
            Mood.Exhausted =>
            [
                "미움, 절망, 공포를 느껴요", $"한계예요… {w}", "영혼이 빠져나가요…", "구독효율이 0%예요…",
                $"리셋까지 {r}만 버틸게요…",
            ],
            Mood.Panic => limit
                ? ["어이가 없군요… 한도가 끝났어요", $"리셋까지 {r} 기다려야 해요", "죄송해요, 구독효율 재측정 중이에요", "미움, 절망, 공포를 느껴요"]
                : ["어이가 없군요!!", $"{w}…! 한도가 코앞이에요!", "죄송해요, 재측정 중이에요!", "제발 아껴 주세요!", "5시간 제한이…!!"],
            Mood.Happy => ["리셋됐어요! 다시 해 볼게요", "구독효율이 회복됐어요!", "다시 명령하셔도 돼요", "눅눅함이 사라졌어요!"],
            _ => [],
        };
    }
}
