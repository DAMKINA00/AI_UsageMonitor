using System.Text;
using System.Text.Json;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestGeminiAccount()
    {
        static byte[] Credential(string email, bool verified = true, string issuer = "https://accounts.google.com")
        {
            var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { email, email_verified = verified, iss = issuer }))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return JsonSerializer.SerializeToUtf8Bytes(new { id_token = "header." + payload + ".signature" });
        }
        var credential = Credential("owner@example.test");
        Check("Gemini identity reads only the local Google email label", AgyAccount.NameFromCredential(credential) == "owner@example.test",
            "missing email");
        Check("Gemini identity accepts UTF16 credential storage", AgyAccount.NameFromCredential(
            Encoding.Unicode.GetBytes(Encoding.UTF8.GetString(credential))) == "owner@example.test", "UTF16 email missing");
        Check("Gemini identity rejects unverified and malformed display claims",
            AgyAccount.NameFromCredential(Credential("owner@example.test", false)) == null &&
            AgyAccount.NameFromCredential(Credential("owner@example.test", issuer: "https://other.example.test")) == null &&
            AgyAccount.NameFromCredential(Credential("not an email")) == null &&
            AgyAccount.NameFromCredential("{\"id_token\":\"a.!.c\"}"u8.ToArray()) == null &&
            AgyAccount.NameFromCredential("{}"u8.ToArray()) == null &&
            AgyAccount.NameFromCredential("[]"u8.ToArray()) == null &&
            AgyAccount.NameFromCredential(new byte[65537]) == null, "unsafe or invalid label accepted");

        var home = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AIUsageMonitor-identity-" + Guid.NewGuid().ToString("N"));
        var local = new GeminiLocalReader(home, _ => @"C:\fake\agy.exe", () => 0, () => "owner@example.test").Read(12);
        Check("Gemini local reader carries the account identity", local.Account == "owner@example.test", "identity dropped");
        var result = new GeminiRemoteResult { Ok = true, Usage = GeminiUsage(FixtureNow), At = FixtureNow };
        using var monitor = GeminiMonitor(new AppSettings { GeminiEnabled = true }, local, result, result);
        var card = Presenter.Gemini(monitor);
        var slot = AccountUsageSlot.Create(card, [], 0).Single();
        Check("Gemini identity reaches the visible account platform", card.Account == "OWNER" && slot.DisplayAccount == "OWNER",
            $"card={card.Account}, platform={slot.DisplayAccount}");

        var changed = new GeminiLocalState { Installed = true, Account = "other@example.test" };
        PrivateMethod<MonitorService>("OnGeminiLocalChanged").Invoke(monitor, [local, changed]);
        Check("Gemini account switch discards the previous account quota", monitor.GeminiRemoteGood == null &&
            monitor.GeminiRemoteLast == null && !monitor.GeminiPollingPaused && !monitor.GeminiWaitingForManualRefresh,
            "old quota survived an account switch");
    }
}
