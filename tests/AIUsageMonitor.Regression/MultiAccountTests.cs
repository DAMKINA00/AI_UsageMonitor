using System.Reflection;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestMultiAccounts()
    {
        Check("account labels contain only the uppercase ID", Fmt.AccountId("damkina@example.test's Organization") == "DAMKINA" &&
            Fmt.AccountId("  sub@example.test  ") == "SUB" && Fmt.AccountId("plain name") == "PLAIN NAME" &&
            Fmt.AccountId(null) == null && Fmt.AccountId("  ") == null, "account formatting");
        var firstId = Guid.NewGuid().ToString("N");
        var secondId = Guid.NewGuid().ToString("N");
        var settings = new AppSettings
        {
            SecondaryAccounts = [
                new() { Id = firstId, Provider = AccountProvider.Claude },
                new() { Id = secondId, Provider = AccountProvider.Claude },
                new() { Provider = AccountProvider.Codex }, new() { Provider = AccountProvider.Codex },
                new() { Id = firstId.ToUpperInvariant() }, new() { Id = "../bad" }, new() { Provider = (AccountProvider)99 },
            ],
        };
        using var service = new SecondaryAccountService(settings);
        Check("invalid and duplicate account records are excluded", service.Accounts.Count == 4 && settings.SecondaryAccounts.Count == 4,
            $"accounts={service.Accounts.Count}");
        var firstPath = SecondaryAccountService.AccountDirectory(firstId);
        var secondPath = SecondaryAccountService.AccountDirectory(secondId);
        bool rejected = false;
        try { SecondaryAccountService.AccountDirectory("../bad"); } catch (ArgumentException) { rejected = true; }
        Check("authentication paths are isolated and traversal is rejected", firstPath != secondPath && rejected &&
            firstPath.StartsWith(System.IO.Path.Combine(Paths.AppData, "accounts") + System.IO.Path.DirectorySeparatorChar), firstPath);

        var startInfo = typeof(CodexAppServer).GetMethod("StartInfo", BindingFlags.NonPublic | BindingFlags.Static)!;
        var psi = (System.Diagnostics.ProcessStartInfo)startInfo.Invoke(null, ["codex.exe", firstPath])!;
        var psi2 = (System.Diagnostics.ProcessStartInfo)startInfo.Invoke(null, ["codex.exe", secondPath])!;
        var primary = (System.Diagnostics.ProcessStartInfo)startInfo.Invoke(null, ["codex.exe", null])!;
        Check("Codex child process uses only its account home", psi.Environment["CODEX_HOME"] == firstPath &&
            psi2.Environment["CODEX_HOME"] == secondPath && psi.WorkingDirectory == firstPath &&
            psi.ArgumentList.Contains("cli_auth_credentials_store=\"file\"") &&
            !psi.UseShellExecute && psi.CreateNoWindow && psi.RedirectStandardOutput &&
            !psi.Environment.ContainsKey("OPENAI_API_KEY") && !psi.Environment.ContainsKey("CODEX_ACCESS_TOKEN"), psi.WorkingDirectory);
        primary.Environment.TryGetValue("CODEX_HOME", out var inheritedHome);
        Check("primary Codex process keeps existing authentication configuration",
            inheritedHome == Environment.GetEnvironmentVariable("CODEX_HOME") &&
            !primary.ArgumentList.Contains("cli_auth_credentials_store=\"file\""), inheritedHome ?? "default home");
        Check("all monitor Codex connections disable unnecessary plugin refreshes",
            new[] { primary, psi, psi2 }.All(info => info.ArgumentList.SequenceEqual(new[]
            {
                "app-server", "-c", "features.plugins=false",
            }.Concat(info == primary ? Array.Empty<string>() : new[] { "-c", "cli_auth_credentials_store=\"file\"" }))),
            "primary and secondary account process arguments");

        var apply = typeof(SecondaryAccountService).GetMethod("ApplyResult", BindingFlags.Static | BindingFlags.NonPublic)!;
        void Apply(SecondaryAccountState account, bool ok, bool login, string? name, UsageSnapshot? usage, string? error) =>
            apply.Invoke(null, [account, ok, login, name, usage, error]);
        var reset = DateTimeOffset.Now.AddDays(2);
        for (int index = 0; index < service.Accounts.Count; index++)
            Apply(service.Accounts[index], true, false, $"sub{index}@example.test", Snapshot("test", DateTimeOffset.Now,
                Window(service.Accounts[index].Settings.Provider == AccountProvider.Claude ? "seven_day" : "codex:w10080", 10 + index, reset)), null);
        var savedUsage = service.Accounts[0].Usage;
        Apply(service.Accounts[0], false, false, null, null, "network");
        Check("failed refresh retains only that account's last usage", ReferenceEquals(savedUsage, service.Accounts[0].Usage) &&
            service.Accounts[0].Status.Contains("이전 사용량") && service.Accounts[1].Usage!.Windows[0].UsedPercent == 11, service.Accounts[0].Status);

        using var monitor = new MonitorService(new AppSettings());
        SetProperty(monitor, nameof(MonitorService.ClaudeWebGood), new ClaudeWebResult
        {
            State = ClaudeWebState.Ok, OrgName = "mainclaude@example.test's Organization",
            Usage = Snapshot("main", DateTimeOffset.Now, Window("seven_day", 60, reset)),
        });
        SetProperty(monitor, nameof(MonitorService.CodexRemoteGood), new CodexRemoteResult
        {
            Ok = true, Email = "maincodex@example.test", Usage = Snapshot("main", DateTimeOffset.Now, Window("codex:w10080", 70, reset)),
        });
        var claude = Presenter.Claude(monitor, settings, () => { });
        var codex = Presenter.Codex(monitor);
        var cards = AccountUsageCard.Create(claude, codex, service);
        Check("primary and secondary cards show IDs without domains", claude.Account == "MAINCLAUDE" && codex.Account == "MAINCODEX" &&
            cards.All(x => !x.DisplayName.Contains('@')), string.Join(',', cards.Select(x => x.DisplayName)));
        Check("multiple accounts have independent displayed usage", cards.Count == 6 && cards.Count(x => x.IsSecondary) == 4 &&
            cards.SelectMany(x => x.Usage).Select(x => x.Value).Order().SequenceEqual(new double[] { 10, 11, 12, 13, 60, 70 }),
            string.Join(',', cards.SelectMany(x => x.Usage).Select(x => x.Value)));
        Apply(service.Accounts[0], false, true, null, null, null);
        Check("login loss clears only affected account", service.Accounts[0].Usage == null && service.Accounts[0].AccountName == null &&
            service.Accounts[1].Usage!.Windows[0].UsedPercent == 11 && claude.MaxUsed == 60 && codex.MaxUsed == 70,
            service.Accounts[0].Status);
    }
}
