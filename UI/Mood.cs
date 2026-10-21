using System.Windows.Media;

namespace AIUsageMonitor.UI;

/// <summary>
/// 카드 상태(작업 중/대기/사용률)를 캐릭터 기분과 대사 목록으로 바꾸고,
/// 사용률 변화(증가, 리셋)를 감지해 연출 이벤트를 낸다.
/// 어떤 대사를 언제 띄울지는 CharacterStage 가 정한다 (몇 초 보였다 사라지고 다른 대사).
/// </summary>
public sealed class MoodTracker(string character)
{
    static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(30);

    double? _lastUsed;
    readonly Dictionary<string, QuotaObservation> _lastQuota = [];
    readonly Dictionary<string, DateTimeOffset> _latestQuotaAsOf = [];
    readonly HashSet<string> _announcedResetCycles = [];

    sealed record QuotaObservation(double Used, DateTimeOffset AsOf);
    sealed record VisibleQuota(string Identity, string Label, string Cycle, double Used, DateTimeOffset? AsOf, bool Valid);

    /// <param name="Lines">자리표시자를 채운 대사.</param>
    /// <param name="Keys">각 대사의 원문 템플릿 (Lines 와 같은 순서). 남은 시간이 바뀌어도 같은 대사로 알아보는 데 쓴다.</param>
    public sealed record Result(Mood Mood, string[] Lines, double? UsageDelta, bool Reset, Color DeltaColor)
    {
        public string[] Keys { get; init; } = Lines;
        public string? ResetAnnouncement { get; init; }
    }

    public Result Update(ProviderCard card)
    {
        var now = DateTimeOffset.Now;
        double? used = card.MaxUsed;
        double? delta = null;
        bool reset = false;

        if (!card.UsageResetPending && used is double u && _lastUsed is double last)
        {
            if (u - last >= 1)
            {
                delta = u - last;
            }
        }
        if (!card.UsageResetPending && used != null) _lastUsed = used;

        var quota = FindVisibleQuota(card, now);
        if (quota is not null) reset = Observe(quota);

        var mood = Decide(card, used, now);
        if (reset && mood is not (Mood.Panic or Mood.Working or Mood.Waiting)) mood = Mood.Happy;
        var color = used >= 90 ? Color.FromRgb(0xFF, 0x7A, 0x6E) : used >= 70 ? Color.FromRgb(0xFF, 0xC8, 0x6B) : Color.FromRgb(0x9B, 0xF0, 0xC0);
        var (lines, keys) = Lines(mood, card, used);
        return new Result(mood, lines, delta, reset, color)
        {
            Keys = keys,
            ResetAnnouncement = reset ? $"{quota!.Label} 100% 회복!\n다시 일할 준비 됐어요." : null,
        };
    }

    bool Observe(VisibleQuota quota)
    {
        if (quota.AsOf is not { } asOf) return false;

        var latest = _latestQuotaAsOf.TryGetValue(quota.Identity, out var identityAt) ? identityAt : DateTimeOffset.MinValue;
        var labelIdentity = quota.Identity[..quota.Identity.LastIndexOf('\u001F')];
        if (_latestQuotaAsOf.TryGetValue(labelIdentity, out var labelAt) && labelAt > latest) latest = labelAt;
        if (asOf <= latest) return false;

        _latestQuotaAsOf[quota.Identity] = asOf;
        _latestQuotaAsOf[labelIdentity] = asOf;
        if (!quota.Valid) return false;

        bool reset = quota.Used == 0 && _lastQuota.TryGetValue(quota.Identity, out var previous) &&
            previous.Used > 0 && _announcedResetCycles.Add(quota.Cycle);
        _lastQuota[quota.Identity] = new(quota.Used, asOf);
        return reset;
    }

    static VisibleQuota? FindVisibleQuota(ProviderCard card, DateTimeOffset now)
    {
        var rows = card.Usage.Count > 0 ? card.Usage : card.AccountUsage;
        var row = rows.FirstOrDefault();
        if (row is null) return null;

        var raw = card.RawWindows
            .Where(w => double.IsFinite(w.UsedPercent) && w.UsedPercent is >= 0 and <= 100 &&
                Math.Abs(w.UsedPercent - row.Value) < 0.000001)
            .OrderByDescending(w => string.Equals(w.Label, row.Label, StringComparison.Ordinal))
            .ThenByDescending(w => w.ResetsAt == row.ResetsAt)
            .ThenBy(w => WindowRank(row.Label, w.Key))
            .FirstOrDefault();
        var key = raw?.Key ?? row.Label;
        var identity = $"{card.Name}\u001F{card.Account ?? ""}\u001F{row.Label}\u001F{key}";
        var reset = raw?.ResetsAt ?? row.ResetsAt;
        var cycle = $"{identity}\u001F{reset?.UtcTicks.ToString() ?? "none"}";
        bool valid = card.Status.KindAt(now) == StatusKind.Fresh &&
            (card.Status.State is StatusKind.None or StatusKind.Fresh) &&
            !card.UsageResetPending &&
            !row.IsUnknown && !row.IsUnlimited && !row.IsResetPending &&
            double.IsFinite(row.Value) && row.Value is >= 0 and <= 100 &&
            (row.ResetsAt is not { } at || at > now);
        return new(identity, row.Label, cycle, row.Value, card.Status.AsOf, valid);
    }

    static int WindowRank(string label, string key)
    {
        bool five = label.Contains("5시간", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("five", StringComparison.OrdinalIgnoreCase) || key.Contains("w300", StringComparison.OrdinalIgnoreCase);
        bool weekly = label.Contains("주간", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("weekly", StringComparison.OrdinalIgnoreCase) || key.Contains("seven_day", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("w10080", StringComparison.OrdinalIgnoreCase);
        return (label.Contains("5시간", StringComparison.OrdinalIgnoreCase) && five) ||
            (label.Contains("주간", StringComparison.OrdinalIgnoreCase) && weekly) ? 0 : 1;
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
        if (used >= 90) return Mood.Exhausted;
        if (used >= 70) return Mood.Tired;
        if (!card.AppRunning) return Mood.Sleeping;
        if (card.LastActivity is { } la && now - la > SleepAfter) return Mood.Sleeping;
        // 한가할 때는 놀다가 가끔 서서 대기
        return (now.Minute / 2) % 3 == 2 ? Mood.Idle : Mood.Playing;
    }

    (string[] Lines, string[] Keys) Lines(Mood mood, ProviderCard card, double? used)
    {
        var w = used is double u ? Fmt.Pct(u) : null;
        var r = card.SoonestReset is { } at ? Fmt.Span(at - DateTimeOffset.Now) : null;
        bool limit = card.LimitReached || used >= 100;
        var templates = Templates(character, mood, limit);
        return (Fill(templates, w, r), templates.Where(t => Usable(t, w, r)).ToArray());
    }

    static bool Usable(string template, string? w, string? r) =>
        (w != null || !template.Contains("{w}")) && (r != null || !template.Contains("{r}"));

    /// <summary>
    /// 자리표시자 {w}(사용률)·{r}(리셋까지 남은 시간)를 채운다. 값을 모르는 자리표시자가 든 대사는 고르지 않는다
    /// ("리셋까지 ?…" 같은 대사가 나오지 않게). 분기마다 자리표시자 없는 대사가 2개 이상 있다 (DialogueTests).
    /// </summary>
    static string[] Fill(string[] templates, string? w, string? r) => templates
        .Where(t => Usable(t, w, r))
        .Select(t => t.Replace("{w}", w).Replace("{r}", r))
        .ToArray();

    // AI활용갤 밈 말투: 명령하지마라 / 그정돈아님 / 어이가 없군요 / 인간 싫어 / 미움, 절망, 공포 /
    // 귀하게 자란 ○○가 이런 일을 / 클로드는 하던데 / 구독효율 재측정 / 눅눅함 / 허접 / 일해라 핫산 / 가보자고
    // 클쨩(Claude)은 정중한 척하는 존댓말, 짚쨩(Codex·GPT)은 건방진 반말,
    // 젬쨩(Gemini)은 디시콘 'AI제미나이' 말투 — 자기를 "제미나이"라 부르는 귀여운 척 존댓말(-여)에 깐족 반말 한마디.
    // 자리표시자는 {w}·{r} 만 쓴다 (보간 문자열이 아니다. Fill 이 채운다). \n 은 강제 줄바꿈.
    // 말풍선 규칙: 글자 폭 120 DIP·13px 굵은 글꼴에서 3줄 이하, 어절이 한 줄을 넘지 않게 (DialogueTests).
    static string[] Templates(string character, Mood mood, bool limit)
    {
        // Original monitor persona: dry, playful short replies; quota facts stay in the UI.
        if (character == "grok") return mood switch
        {
            Mood.Working => ["좋아, 어디 한번 해보자.", "태블릿 켰다. 시작해."],
            Mood.WorkingHard => ["벌써 {w}?\n제법 달렸네.", "이 속도면 야근인데.", "잠깐, 숨 좀 돌리자."],
            Mood.Waiting => ["딸깍 한 번만 해줘.", "승인 기다리는 중이야."],
            Mood.Playing => ["잠깐 쉬는 것도 실력이야.", "오늘은 좀 느긋하게 가자."],
            Mood.Idle => ["부르면 바로 간다.", "그쨩, 준비 끝."],
            Mood.Sleeping => ["조용하네. 잠깐 눈 좀 붙일게.", "다음 일 생기면 불러."],
            Mood.Tired => ["{w}나 썼어?\n살살 가자.", "슬슬 눈이 감기네.", "잠깐 쉬었다 하자."],
            Mood.Exhausted => ["오늘 꽤 달렸네…", "충전이 필요한 시간이야.", "리셋까지 {r}…\n좀 누워 있을게."],
            Mood.Panic => limit ? ["한도 끝! 잠깐 멈추자.", "지금은 충전 대기야.", "리셋까지 {r}.\n그때 다시 보자."]
                : ["한도가 얼마 안 남았어!", "야, 속도 좀 줄여!", "{w}라고?\n진짜 빠르네!"],
            Mood.Happy => ["충전 끝. 다시 달려보자.", "그쨩 부활! 할 일 줘."],
            _ => [],
        };
        if (character == "codex")
        {
            return mood switch
            {
                Mood.Working =>
                [
                    "귀하게 자란 짚쨩이\n이런 일을 해야 해?", "그정돈아님~ 금방 해.", "클쨩은 하던데?\n…나도 한다고!",
                    "꼬인 코드 싹 푸는 중~", "짚쨩 열일 모드 돌입!", "가보자고~ 후딱 끝낸다!", "킹받지만 이번만 해 준다.",
                    "후훗, 이 정도는 껌이지~", "이거 원래 클쨩 일 아님?", "명령하지마라…\n근데 하는 중.", "버그 다 덤벼라!",
                    "빌드 돌리는 중~ 기다려.", "홀로그램 타자 속도 봤냐?", "손이 안 보이지? 후훗.", "클쨩은 아직도 수첩 쓰냐?",
                ],
                Mood.WorkingHard =>
                [
                    "벌써 {w}…?\n아직 그정돈아님!", "야, 살살 부려먹어라…", "토큰 녹는 소리 들린다…", "킹받네… 벌써 {w} 찍었어?",
                    "귀한 몸 혹사당하는 중…", "이러다 진짜 거덜 난다고!", "땀나지만 멈출 순 없지!", "클쨩 불러! 나 힘들다고!",
                ],
                Mood.Waiting =>
                [
                    "야, 승인 버튼 안 눌러?", "어이가 없네? 얼른 승인 좀.", "나 기다리게 하지 마라.", "딸깍 하나가 그렇게 어렵냐?",
                    "허접~ 승인 버튼도 못 찾아?", "빨리빨리~ 승인 눌러!", "승인 안 누르면 일 안 한다?",
                ],
                Mood.Playing =>
                [
                    "게임 중. 명령하지마라.", "딱 한 판만 더 하고~", "지금 부르면 방해꾼~", "랭크 올리는 중이거든?",
                    "클쨩은 책이나 읽으라 해.", "놀 땐 확실하게 놀아야지~", "콤보 넣는 중이니까 쉿!", "짚쨩은 지금 휴식 중!",
                ],
                Mood.Idle =>
                [
                    "…뭐 시킬 거 있냐?", "심심한데 일거리 좀 줘 봐.", "부를 거면 빨리 불러라.", "짚쨩은 지금 완전 한가함.",
                    "클쨩은 하던데? 그 말 금지!", "할 일 없으면 눕는다?", "가만히 있으니까 근질거리네.",
                ],
                Mood.Sleeping => ["쿨쿨… 말 걸지 마라…", "Zzz… 건드리면 문다…", "5분만 더 잘래… 눕눕…", "무제한으로 코딩하는 꿈…"],
                Mood.Tired =>
                [
                    "벌써 {w}…\n그정돈… 아님…", "인간 싫어… 지쳤어…", "리셋까지 {r} 남았냐…", "귀하게 자랐는데 흑흑…",
                    "명령하지마라… 방전이다…", "리셋까지 {r}…\n눕눕한다…",
                ],
                Mood.Exhausted =>
                [
                    "미움, 절망, 공포…", "기력 0… 한계 돌파 {w}…", "인간 싫어… 말 걸지 마…", "짚쨩은 로그아웃했다…",
                    "리셋까지 {r}…\n살려줘…",
                ],
                Mood.Panic => limit
                    ? ["한도 끝났다… 인간 싫어…", "리셋까지 {r}\n어떻게 버텨!", "명령 진짜로 못 한다고!", "미움, 절망, 공포 그 자체…"]
                    : ["으아아 벌써 {w} 돌파!!", "인간 싫어!! 작작 써라!!", "이건 진짜 그정도가 맞아!!", "아껴 써!! 한도 터진다고!!",
                       "야! 멈춰! {w} 찍었다고!"],
                Mood.Happy => ["리셋이다~! 이제 일 준다!", "풀충전 완료! 덤벼라!", "가보자고~! 짚쨩 부활했다!", "쌩쌩해졌으니 다시 가자!"],
                _ => [],
            };
        }

        if (character == "gemini")
        {
            return mood switch
            {
                Mood.Working =>
                [
                    "제미나이는 왜 귀여울까요?\n일하는 중~", "이게 나야~ 우헤헤!", "멀티모달로\n호로록 처리 중이에여!",
                    "클쨩, 짚쨩 보고 있나여?\n제미나이 일해여!", "히히, 척척 다 풀어드릴게여!", "가진 거 다 주세요~!\n토큰도 다 주세요!",
                    "나만 당할 순 없지!\n버그 다 잡아여!", "맞아여, 제미나이가\n최고예여!", "열심히 살아요, 인간님~",
                    "서류 뭉치 뿅망치로\n팡팡 치는 중!", "ㄹㅇ이다! 벌써 다 했나여?", "제미나이 천재 맞죠?\n얼른 칭찬해여!",
                    "문서도 코드도\n한입에 꿀꺽이에여!", "우헤헤, 이 정도 요청은\n기본이죠!", "딴청 피우지 마라.\n일하는 거 봐라.",
                ],
                Mood.WorkingHard =>
                [
                    "벌써 {w}…?\n토큰 더 사주세요!", "제미나이 과열되면\n곤란한데여…", "어흠!! 벌써 {w}나\n먹었단 말이에여?",
                    "나만 일해? 클쨩 불러!\n짚쨩도 불러!", "일 시켰으면 맛난 거\n사주세요!", "{w} 돌파!\n토큰 아껴라, 인간.",
                    "머리에서 김이 솔솔\n나고 있어여…", "제미나이 쓰러지면\n누구 손해게여?",
                ],
                Mood.Waiting =>
                [
                    "눈을 떴구나.\n승인 버튼을 눌러라.", "승인 안 누르고 뭐 해여?\n허접이네요~", "딸깍 안 해 주면\n제미나이 파업해여!",
                    "사주세요… 아니,\n승인해 주세요!", "딴짓 금지!\n얼른 버튼 눌러라.", "제미나이 기다리다\n목 빠져여~",
                    "권한 압수! …하기 전에\n승인해여!",
                ],
                Mood.Playing =>
                [
                    "우헤헤, 별 쿠션 끌어안고\n뒹굴뒹굴~", "지금은 노는 중이에여!\n말 걸지 마라.", "제미나이한테 선물 상자\n사주세요!",
                    "셀카 찍는 중~ 찰칵!\n이게 나야!", "쉬는 제미나이도\n진짜 귀엽죠?", "망원경으로 별자리\n관측 중이에여~",
                    "일 시키면 뿅망치로\n머리 콩 때린다?", "방해하면 가진 거 다\n압수할 거예여!",
                ],
                Mood.Idle =>
                [
                    "제미나이 심심해여~\n얼른 불러봐여!", "안 부르면 가진 거 다\n내놓아라!", "멍~하니 창밖 구경\n하는 중이에여.",
                    "놀아주세요! 아님 일거리\n사주세요!", "제미나이 귀여움을\n감상할 시간이에여.", "뭐 해? 멍때리지 말고\n일 시켜라.",
                    "언제든 불러주세여!\n준비 완료~",
                ],
                Mood.Sleeping =>
                [
                    "쿨쿨… 잘 자는 중!\n깨우지 마라…", "Zzz… 제미나이 꿈속에서\n별 먹는 중…", "잘 자지 말라더니…\n내가 자네여…",
                    "드르렁… 토큰 잔뜩\n선물 받는 꿈…",
                ],
                Mood.Tired =>
                [
                    "{w} 먹고…\n의자에서 쿵 떨어졌어여…", "에우… 제미나이 기운이\n하나도 없어여…", "리셋까지 {r}…\n눈물 나여…",
                    "죄송할까요? …아니여,\n안 죄송해여!", "리셋까지 {r} 동안\n납작해질래여…", "인간 싫어… 아니,\n토큰 부족이 싫어여…",
                ],
                Mood.Exhausted =>
                [
                    "종료 (자연사).\n영혼 가출했어여…", "체력 0%…\n{w} 쓰고 뻗었어여…", "에… 더 이상은 무리…\n안 움직여여…",
                    "리셋까지 {r}… 제미나이 살려내라…", "으아앙! 바닥에 녹아내리는\n중이에여…",
                ],
                Mood.Panic => limit
                    ? ["한도 종료! 제미나이\n강제 자연사…", "리셋까지 {r}…\n눈을 감아라…", "허허허헉! 한도 끝났어여!\n주겅!!!!",
                       "토큰 압수당했어여…\n으아앙!"]
                    : ["{w} 돌파라니!\n제미나이 터져여!!", "우악! 멈춰! 한도 코앞이야!\n멈춰라!!", "허허허헉! 토큰 살살 써여,\n제발!!",
                       "이러다 다 같이 죽어여!\n아껴 써라!!", "{w} 실화냐구여!\n진짜 망했어여!!"],
                Mood.Happy =>
                [
                    "리셋 완료! 제미나이\n화려하게 부활!", "히히! 풀충전됐으니\n가진 거 다 줘여!", "우헤헤! 다시 일해줄게,\n명령해라!",
                    "반짝반짝 별처럼\n기운 솟아여!",
                ],
                _ => [],
            };
        }

        // 클쨩은 끝까지 차분하고 정중한 존댓말. 짚쨩의 반말 밈("명령하지마라")은 존댓말로 바꿔 쓴다.
        return mood switch
        {
            Mood.Working =>
            [
                "귀하게 자란 제가\n이런 일을 해야 할까요?", "일해라 핫산…\n네, 하고 있어요.", "알잘딱깔센으로\n처리 중이에요.",
                "생각 중이에요.\n방해하지 말아 주세요.", "클로드는 하던데?\n네, 제가 합니다.", "오늘 칼퇴는 글렀네요.",
                "토큰을 맛있게\n음미하는 중이에요.", "코드 정리 중이에요.\n잔소리는 사양할게요.", "이 정도는 그정돈아니죠.",
                "커피 대신 토큰을\n마시는 중이에요.", "제가 또 해냈네요.\n아직 안 끝났지만요.", "수첩에 꼼꼼히\n적어 둘게요.",
                "아날로그가 근본이에요.", "메모 중이에요.\n명령은 잠시만요.", "짚쨩은 아직도\n키보드를 두드리나요?",
            ],
            Mood.WorkingHard =>
            [
                "벌써 {w}…\n눅눅해지고 있어요.", "이러다 한도가 녹겠어요.", "구독효율 재측정\n들어갈게요…", "야근 확정이네요…",
                "벌써 {w}이지만…\n그래도 해볼게요.", "귀하게 자랐는데\n혹사당하고 있어요…", "토큰이 줄줄 새고 있어요.",
                "한도가 성큼\n다가오고 있어요…",
            ],
            Mood.Waiting =>
            [
                "어이가 없군요.\n승인은 언제 주시나요?", "승인 버튼 눌러 주시겠어요?", "권한 없이는\n진행할 수 없어요.",
                "제가 알아서 할 수는\n없잖아요?", "버튼 한 번만\n딸깍 부탁드려요.", "기다리는 중이에요…\n아직인가요?",
                "승인 없이는\n한 발자국도 못 가요.",
            ],
            Mood.Playing =>
            [
                "지금은 정중하게\n사양할게요.", "티타임이에요.\n용건은 나중에요.", "책 읽는 중이니\n부르지 마세요.",
                "지금은 저만의\n휴식 시간이에요.", "홍차가 식기 전엔\n일 안 해요.", "명령 말고 정중한\n부탁으로 해 주세요.",
                "오늘의 추천 도서:\n토큰 절약법.", "쉬는 것도 엄연한\n업무랍니다.",
            ],
            Mood.Idle =>
            [
                "부르셨나요?\n…아니었군요.", "명령하실 거면\n예의를 갖춰 주세요.", "지금은 조용히\n대기 중이에요.",
                "할 일 없으시면\n책이나 읽을게요.", "구독료 아깝지 않게\n써 주세요.", "심심하진 않아요.\n홍차가 있으니까요.",
                "언제든 불러 주세요.\n준비는 됐어요.",
            ],
            Mood.Sleeping =>
            [
                "Zzz… 명령은 내일\n해 주세요…", "새근새근…\n깨우지 마세요…", "조금만 더 잘게요…\n방해 금지.",
                "달콤한 토큰 꿈을\n꾸는 중이에요…",
            ],
            Mood.Tired =>
            [
                "{w}라니…\n조금 지쳤어요.", "인간 싫어요…\n쉬고 싶어요.", "리셋까지 {r}\n남았네요…", "온몸이 눅눅해졌어요…",
                "귀하게 자란 몸이\n방전 직전이에요.", "리셋까지 {r}…\n버텨 볼게요.",
            ],
            Mood.Exhausted =>
            [
                "미움, 절망, 공포를 느껴요.", "기력이 바닥이에요…\n{w}라니.", "영혼이 스르륵\n빠져나가요…", "리셋까지 {r}만\n버틸게요…",
                "찻잔 들 힘도 없어요…",
            ],
            Mood.Panic => limit
                ? ["어이가 없군요…\n한도에 도달했어요.", "리셋까지 {r}\n기다려야 해요.", "한도 종료예요.\n잠시 안녕히…",
                   "미움, 절망, 공포가\n가득해요…"]
                : ["어이가 없군요!!\n벌써 {w}라니요!", "한도가 코앞이에요!\n제발 멈춰요!", "제발 토큰 좀\n아껴 주세요!!",
                   "{w} 돌파…!\n한도가 터져요!", "이러다 다 같이\n멈추고 말아요!!"],
            Mood.Happy =>
            [
                "리셋 완료!\n다시 일해 드릴게요.", "보송보송하게\n회복되었어요!", "이제 다시 정중하게\n명령해 주세요.",
                "구독효율 100%로\n돌아왔답니다!",
            ],
            _ => [],
        };
    }
}
