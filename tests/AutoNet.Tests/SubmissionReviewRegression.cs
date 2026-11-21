using System.Net;
using AutoNet.Core;

internal static class SubmissionReviewRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("提交复核回归：首次复核按设置、恢复开关、链路顺序拒绝", () =>
        {
            var settings = new Settings
            {
                InterfaceId = "00000000-0000-0000-0000-000000000001", WifiProfile = "fixture", Enabled = true
            };
            var link = new WifiLink(true, null, IPAddress.Parse("10.0.0.1"), 7, []);
            Require(SubmissionReview.ProtocolInitial(settings, settings, link, link) is null, "一致时仍拒绝");

            Require(SubmissionReview.ProtocolInitial(null, settings, link, link) == "SettingsChanged",
                "缺少设置未报告 SettingsChanged");
            Require(SubmissionReview.ProtocolInitial(settings with { WifiProfile = "other" }, settings, link, link) == "SettingsChanged",
                "设置变化未报告 SettingsChanged");
            var disabled = settings with { Enabled = false };
            Require(SubmissionReview.ProtocolInitial(disabled, disabled, link, link) == "RecoveryDisabled",
                "恢复开关关闭未报告 RecoveryDisabled");
            Require(SubmissionReview.ProtocolInitial(settings, settings, link with { Connected = false }, link) == "WifiChanged",
                "链路未就绪未报告 WifiChanged");
            Require(SubmissionReview.ProtocolInitial(settings, settings, link with { Index = 9 }, link) == "WifiChanged",
                "链路索引变化未报告 WifiChanged");
            Require(SubmissionReview.ProtocolInitial(settings, settings, link with { Address = IPAddress.Parse("10.0.0.9") }, link) == "WifiChanged",
                "链路地址变化未报告 WifiChanged");
            return Task.CompletedTask;
        }),
        ("提交复核回归：最终复核先看认证与额度，再看时钟与设置链路", () =>
        {
            var settings = new Settings
            {
                InterfaceId = "00000000-0000-0000-0000-000000000001", WifiProfile = "fixture", Enabled = true
            };
            var link = new WifiLink(true, null, IPAddress.Parse("10.0.0.1"), 7, []);
            var authentication = new AuthenticationBlock { State = AuthenticationState.SubmissionPending };
            var budget = new SubmissionBudget { State = SubmissionBudgetState.Reserved, Attempts = 1 };
            var authorized = new SubmissionReviewFacts(settings, link, authentication, budget);

            Require(SubmissionReview.ProtocolFinal(authorized, authorized, true) is null, "一致时仍拒绝");
            Require(SubmissionReview.ProtocolFinal(authorized with { Authentication = null }, authorized, true)
                == "FinalAuthenticationOrBudgetChanged", "认证变化未报告");
            Require(SubmissionReview.ProtocolFinal(authorized with { Budget = null }, authorized, true)
                == "FinalAuthenticationOrBudgetChanged", "额度变化未报告");
            Require(SubmissionReview.ProtocolFinal(authorized, authorized, false) == "FinalClockUntrusted",
                "时钟不可信未报告");
            Require(SubmissionReview.ProtocolFinal(authorized with { Settings = settings with { WifiProfile = "other" } },
                authorized, true) == "FinalSettingsOrWifiChanged", "设置变化未报告");
            Require(SubmissionReview.ProtocolFinal(authorized with { Link = link with { Index = 9 } },
                authorized, true) == "FinalSettingsOrWifiChanged", "链路变化未报告");

            // 顺序固定：认证/额度问题优先于时钟与设置，日志原因不因组合变化而漂移。
            var multiple = authorized with { Authentication = null, Settings = null };
            Require(SubmissionReview.ProtocolFinal(multiple, authorized, false) == "FinalAuthenticationOrBudgetChanged",
                "最终复核未先看认证与额度");
            return Task.CompletedTask;
        }),
        ("提交复核回归：通道复核覆盖凭据、浏览器、复核窗口与时钟", () =>
        {
            var settings = new Settings
            {
                InterfaceId = "00000000-0000-0000-0000-000000000001", WifiProfile = "fixture", Enabled = true
            };
            var link = new WifiLink(true, null, IPAddress.Parse("10.0.0.1"), 7, []);
            var authentication = new AuthenticationBlock { State = AuthenticationState.SubmissionPending };
            var budget = new SubmissionBudget { State = SubmissionBudgetState.Reserved, Attempts = 1 };
            var credentials = new Credentials("fixture", "never-real");
            var authorized = new SubmissionReviewFacts(settings, link, authentication, budget, credentials);

            Require(SubmissionReview.ChannelStillValid(authorized, authorized, true, true, true) is null, "一致时仍拒绝");
            Require(SubmissionReview.ChannelStillValid(authorized with { Settings = null }, authorized, true, true, true)
                == "SettingsChanged", "设置变化未报告");
            Require(SubmissionReview.ChannelStillValid(authorized with { Link = link with { Connected = false } },
                authorized, true, true, true) == "WifiChanged", "链路变化未报告");
            Require(SubmissionReview.ChannelStillValid(
                authorized with { Credentials = new Credentials("fixture", "changed") },
                authorized, true, true, true) == "CredentialsChanged", "凭据变化未报告");
            Require(SubmissionReview.ChannelStillValid(authorized, authorized, false, true, true) == "BrowserUnbound",
                "浏览器未绑定未报告");
            Require(SubmissionReview.ChannelStillValid(authorized with { Authentication = null }, authorized, true, true, true)
                == "AuthenticationStateChanged", "认证变化未报告");
            Require(SubmissionReview.ChannelStillValid(authorized with { Budget = null }, authorized, true, true, true)
                == "SubmissionBudgetChanged", "额度变化未报告");
            Require(SubmissionReview.ChannelStillValid(authorized, authorized, true, false, true) == "ReviewChainTimeout",
                "复核窗口耗尽未报告");
            Require(SubmissionReview.ChannelStillValid(authorized, authorized, true, true, false) == "FinalClockUntrusted",
                "时钟不可信未报告");

            var multiple = authorized with { Credentials = null, Link = link with { Index = 9 } };
            Require(SubmissionReview.ChannelStillValid(multiple, authorized, false, false, false) == "WifiChanged",
                "通道复核未先看链路");
            return Task.CompletedTask;
        }),
        ("提交复核回归：时钟可信要求墙钟与单调时钟一致", () =>
        {
            var clock = new ManualClock();
            var utc = clock.GetUtcNow();
            var tick = clock.GetTimestamp();
            Require(SubmissionReview.ClockTrusted(clock, utc, tick, TimeSpan.FromSeconds(2)), "静止时钟被判定不可信");
            clock.Advance(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
            Require(!SubmissionReview.ClockTrusted(clock, utc, tick, TimeSpan.FromSeconds(2)),
                "墙钟跳变未被判定为不可信");
            return Task.CompletedTask;
        })
    ];

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
