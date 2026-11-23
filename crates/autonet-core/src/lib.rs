//! `AutoNet.Core.RecoveryPolicy` 的只读对照实现。
//!
//! 目标不是“重新设计策略”，而是让 Rust 与 C# 在同一组观察序列上给出同样的动作、原因与退避时间。
//! 所有时间都是 Unix 毫秒；策略内部不读时钟、不做 I/O。夹具来自
//! `fixtures/cross-language/recovery-policy.json`（由 C# 导出），测试逐字段比对。

#![forbid(unsafe_code)]

use std::collections::HashMap;

pub const FAST_RETRY_SECONDS: i64 = 2;
pub const FAST_RETRY_WINDOW_SECONDS: i64 = 15;
pub const BACKOFF_CEILING_SECONDS: i64 = 60;
pub const DISCONNECT_COOLDOWN_SECONDS: i64 = 120;
pub const DISCONNECT_ATTEMPT_CAP: i32 = 3;
pub const STALLED_ROUND_THRESHOLD: i32 = 3;
pub const STALLED_OBSERVATION_SECONDS: i64 = 30;
pub const ONLINE_RESET_SECONDS: i64 = 60;
pub const AUTHENTICATION_ATTEMPT_CAP: i32 = 3;

const SECOND_MILLIS: i64 = 1000;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum ActionKind {
    #[default]
    None,
    ReconnectWifi,
    Authenticate,
    DisconnectWifi,
}

impl ActionKind {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::None => "None",
            Self::ReconnectWifi => "ReconnectWifi",
            Self::Authenticate => "Authenticate",
            Self::DisconnectWifi => "DisconnectWifi",
        }
    }

    pub fn parse(value: &str) -> Option<Self> {
        Some(match value {
            "None" => Self::None,
            "ReconnectWifi" => Self::ReconnectWifi,
            "Authenticate" => Self::Authenticate,
            "DisconnectWifi" => Self::DisconnectWifi,
            _ => return None,
        })
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WifiState {
    Unavailable,
    Disconnected,
    Associating,
    WaitingForAddress,
    Ready,
    OtherProfile,
}

impl WifiState {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Unavailable => "Unavailable",
            Self::Disconnected => "Disconnected",
            Self::Associating => "Associating",
            Self::WaitingForAddress => "WaitingForAddress",
            Self::Ready => "Ready",
            Self::OtherProfile => "OtherProfile",
        }
    }

    pub fn parse(value: &str) -> Option<Self> {
        Some(match value {
            "Unavailable" => Self::Unavailable,
            "Disconnected" => Self::Disconnected,
            "Associating" => Self::Associating,
            "WaitingForAddress" => Self::WaitingForAddress,
            "Ready" => Self::Ready,
            "OtherProfile" => Self::OtherProfile,
            _ => return None,
        })
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PortalState {
    Unknown,
    LoggedIn,
    NeedsLogin,
}

impl PortalState {
    pub fn parse(value: &str) -> Option<Self> {
        Some(match value {
            "Unknown" => Self::Unknown,
            "LoggedIn" => Self::LoggedIn,
            "NeedsLogin" => Self::NeedsLogin,
            _ => return None,
        })
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NetworkCondition {
    Online,
    InterfaceUnavailable,
    WifiDisconnected,
    Associating,
    WaitingForAddress,
    OtherProfile,
    PortalUnknown,
    NeedsAuthentication,
    AuthenticatedOffline,
}

impl NetworkCondition {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Online => "Online",
            Self::InterfaceUnavailable => "InterfaceUnavailable",
            Self::WifiDisconnected => "WifiDisconnected",
            Self::Associating => "Associating",
            Self::WaitingForAddress => "WaitingForAddress",
            Self::OtherProfile => "OtherProfile",
            Self::PortalUnknown => "PortalUnknown",
            Self::NeedsAuthentication => "NeedsAuthentication",
            Self::AuthenticatedOffline => "AuthenticatedOffline",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Observation {
    pub wifi: WifiState,
    pub direct_online: bool,
    pub portal: PortalState,
}

impl Observation {
    pub fn new(wifi: WifiState, direct_online: bool, portal: PortalState) -> Self {
        Self {
            wifi,
            direct_online,
            portal,
        }
    }

    pub fn condition(self) -> NetworkCondition {
        if self.direct_online {
            return NetworkCondition::Online;
        }
        match self.wifi {
            WifiState::Unavailable => NetworkCondition::InterfaceUnavailable,
            WifiState::Disconnected => NetworkCondition::WifiDisconnected,
            WifiState::Associating => NetworkCondition::Associating,
            WifiState::WaitingForAddress => NetworkCondition::WaitingForAddress,
            WifiState::OtherProfile => NetworkCondition::OtherProfile,
            WifiState::Ready => match self.portal {
                PortalState::LoggedIn => NetworkCondition::AuthenticatedOffline,
                PortalState::NeedsLogin => NetworkCondition::NeedsAuthentication,
                PortalState::Unknown => NetworkCondition::PortalUnknown,
            },
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DisconnectRequestState {
    Completed,
    RequestPending,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DisconnectBudget {
    pub attempts: i32,
    pub state: DisconnectRequestState,
    pub completed_at: Option<i64>,
}

impl DisconnectBudget {
    pub fn new(attempts: i32, state: DisconnectRequestState, completed_at: Option<i64>) -> Self {
        Self {
            attempts,
            state,
            completed_at,
        }
    }
}

#[derive(Debug, Default)]
pub struct RecoveryPolicy {
    last_action: ActionKind,
    stalled_since: Option<i64>,
    online_since: Option<i64>,
    stalled_rounds: i32,
    disconnect_pending: bool,
    disconnect_completed_at: Option<i64>,
    retries: HashMap<ActionKind, (i32, i64)>,
    disconnect_attempts: i32,
    next_disconnect_attempt: Option<i64>,
    outage_since: Option<i64>,
    next_attempt: Option<i64>,
    next_attempt_action: ActionKind,
    attempts: i32,
    credentials_blocked: bool,
    decision_reason: String,
}

impl RecoveryPolicy {
    pub fn new() -> Self {
        Self {
            decision_reason: "NotEvaluated".to_string(),
            ..Self::default()
        }
    }

    pub fn decide(
        &mut self,
        observation: Observation,
        now: i64,
        enabled: bool,
        has_credentials: bool,
        allow_active_disconnect: bool,
    ) -> ActionKind {
        if observation.direct_online {
            let since = self.online_since.unwrap_or(now);
            self.reset();
            self.online_since = Some(since);
            if !self.disconnect_pending && now - since >= ONLINE_RESET_SECONDS * SECOND_MILLIS {
                self.disconnect_attempts = 0;
                self.next_disconnect_attempt = None;
                self.disconnect_completed_at = None;
            }
            return self.no_action("Online");
        }
        self.online_since = None;
        if !enabled {
            self.reset();
            return self.no_action("RecoveryDisabled");
        }
        if self.outage_since.is_none() {
            self.outage_since = Some(now);
        }
        let condition = observation.condition();
        let stalled = matches!(
            condition,
            NetworkCondition::PortalUnknown | NetworkCondition::AuthenticatedOffline
        ) && has_credentials
            && !self.credentials_blocked
            && allow_active_disconnect;
        if stalled {
            if self.stalled_since.is_none() {
                self.stalled_since = Some(now);
            }
            self.stalled_rounds += 1;
        } else {
            self.stalled_since = None;
            self.stalled_rounds = 0;
        }
        let action = if observation.wifi == WifiState::Disconnected {
            ActionKind::ReconnectWifi
        } else if condition == NetworkCondition::NeedsAuthentication
            && has_credentials
            && !self.credentials_blocked
        {
            ActionKind::Authenticate
        } else if stalled {
            ActionKind::DisconnectWifi
        } else {
            ActionKind::None
        };
        self.next_attempt = None;
        self.next_attempt_action = ActionKind::None;
        if action == ActionKind::None {
            let reason = if observation.wifi != WifiState::Ready {
                condition.as_str()
            } else if !has_credentials {
                "CredentialsMissing"
            } else if self.credentials_blocked {
                "AuthenticationPaused"
            } else if !allow_active_disconnect {
                "ActiveDisconnectDisabled"
            } else {
                "NoApplicableAction"
            };
            return self.no_action(reason);
        }
        if action == ActionKind::DisconnectWifi
            && (self.disconnect_attempts >= DISCONNECT_ATTEMPT_CAP || self.disconnect_pending)
        {
            return self.no_action(if self.disconnect_pending {
                "DisconnectCompletionUnconfirmed"
            } else {
                "DisconnectBudgetExhausted"
            });
        }
        self.next_attempt = self.retries.get(&action).map(|(_, next)| *next);
        self.next_attempt_action = action;
        if action == ActionKind::DisconnectWifi {
            let stalled_since = self.stalled_since.unwrap_or(now);
            let window_end = stalled_since + STALLED_OBSERVATION_SECONDS * SECOND_MILLIS;
            self.next_attempt = match self.next_disconnect_attempt {
                Some(next) if next > window_end => Some(next),
                _ => Some(window_end),
            };
            if self.stalled_rounds < STALLED_ROUND_THRESHOLD {
                return self.no_action("InsufficientFailureSamples");
            }
        }
        if self.next_attempt.is_some_and(|next| now < next) {
            let reason = if action == ActionKind::DisconnectWifi {
                let observation_end =
                    self.stalled_since.unwrap_or(now) + STALLED_OBSERVATION_SECONDS * SECOND_MILLIS;
                if now < observation_end {
                    "ObservationWindow"
                } else {
                    "DisconnectWifiCooldown"
                }
            } else {
                match action {
                    ActionKind::ReconnectWifi => "ReconnectWifiCooldown",
                    ActionKind::Authenticate => "AuthenticateCooldown",
                    _ => "Cooldown",
                }
            };
            return self.no_action(reason);
        }
        self.last_action = action;
        self.decision_reason = format!("{}Required", action.as_str());
        action
    }

    pub fn attempted(&mut self, finished_at: i64) {
        self.attempts += 1;
        let action_attempts = self
            .retries
            .get(&self.last_action)
            .map(|(attempts, _)| attempts + 1)
            .unwrap_or(1);
        let fast = self
            .outage_since
            .is_some_and(|start| finished_at - start < FAST_RETRY_WINDOW_SECONDS * SECOND_MILLIS);
        let delay = if self.last_action == ActionKind::DisconnectWifi {
            DISCONNECT_COOLDOWN_SECONDS
        } else if fast {
            FAST_RETRY_SECONDS
        } else {
            let exponent = (action_attempts - 3).clamp(0, 3);
            BACKOFF_CEILING_SECONDS.min(10 * (1i64 << exponent))
        };
        let next = finished_at + delay * SECOND_MILLIS;
        self.next_attempt = Some(next);
        self.next_attempt_action = self.last_action;
        self.retries
            .insert(self.last_action, (action_attempts, next));
        if self.last_action == ActionKind::DisconnectWifi {
            self.disconnect_attempts += 1;
            self.next_disconnect_attempt = Some(next);
            self.disconnect_pending = true;
        }
    }

    pub fn restore_disconnect_budget(&mut self, budget: DisconnectBudget, now: i64) {
        self.disconnect_attempts = budget.attempts;
        self.disconnect_pending = budget.state == DisconnectRequestState::RequestPending;
        self.disconnect_completed_at = budget.completed_at;
        // 单调时钟不能跨重启；恢复时重新计满冷却，重启不能缩短等待。
        self.next_disconnect_attempt = if budget.attempts == 0 {
            None
        } else {
            Some(now + DISCONNECT_COOLDOWN_SECONDS * SECOND_MILLIS)
        };
    }

    pub fn disconnect_completed(&mut self, budget: DisconnectBudget, now: i64) {
        self.disconnect_pending = false;
        self.disconnect_completed_at = budget.completed_at;
        let next = now + DISCONNECT_COOLDOWN_SECONDS * SECOND_MILLIS;
        self.next_attempt = Some(next);
        self.next_disconnect_attempt = Some(next);
        self.next_attempt_action = ActionKind::DisconnectWifi;
    }

    pub fn cancel_unissued_disconnect_reservation(&mut self) {
        self.disconnect_pending = false;
    }

    pub fn interrupt_observation(&mut self) {
        self.stalled_since = None;
        self.online_since = None;
        self.stalled_rounds = 0;
    }

    pub fn defer_authentication_until(&mut self, until: i64) {
        let retry = self
            .retries
            .get(&ActionKind::Authenticate)
            .copied()
            .unwrap_or((0, i64::MIN));
        if until > retry.1 {
            self.retries
                .insert(ActionKind::Authenticate, (retry.0, until));
        }
    }

    pub fn reset(&mut self) {
        self.outage_since = None;
        self.next_attempt = None;
        self.next_attempt_action = ActionKind::None;
        self.attempts = 0;
        self.last_action = ActionKind::None;
        self.stalled_since = None;
        self.stalled_rounds = 0;
        self.online_since = None;
        self.retries.clear();
        // 持久断线额度与认证暂停跨瞬时重置保留。
    }

    pub fn set_credentials_blocked(&mut self, blocked: bool) {
        self.credentials_blocked = blocked;
    }

    pub fn credentials_blocked(&self) -> bool {
        self.credentials_blocked
    }

    pub fn attempts(&self) -> i32 {
        self.attempts
    }

    pub fn disconnect_attempts(&self) -> i32 {
        self.disconnect_attempts
    }

    pub fn next_attempt(&self) -> Option<i64> {
        self.next_attempt
    }

    pub fn next_attempt_action(&self) -> ActionKind {
        self.next_attempt_action
    }

    pub fn outage_since(&self) -> Option<i64> {
        self.outage_since
    }

    pub fn failed_rounds(&self) -> i32 {
        self.stalled_rounds
    }

    pub fn observation_seconds(&self, now: i64) -> f64 {
        match self.stalled_since {
            Some(since) => ((now - since).max(0) as f64) / 1000.0,
            None => 0.0,
        }
    }

    pub fn decision_reason(&self) -> &str {
        &self.decision_reason
    }

    fn no_action(&mut self, reason: &str) -> ActionKind {
        self.decision_reason = reason.to_string();
        ActionKind::None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn 断连优先重连且快重试后封顶() {
        let mut policy = RecoveryPolicy::new();
        let observation = Observation::new(WifiState::Disconnected, false, PortalState::Unknown);
        assert_eq!(
            policy.decide(observation, 0, true, true, true),
            ActionKind::ReconnectWifi
        );
        policy.attempted(0);
        assert_eq!(policy.next_attempt(), Some(2_000));
        assert_eq!(
            policy.decide(observation, 1_000, true, true, true),
            ActionKind::None
        );
        assert_eq!(policy.decision_reason(), "ReconnectWifiCooldown");
        assert_eq!(
            policy.decide(observation, 2_000, true, true, true),
            ActionKind::ReconnectWifi
        );
    }

    #[test]
    fn 认证暂停不提交而断连完成未确认保持阻断() {
        let mut policy = RecoveryPolicy::new();
        policy.set_credentials_blocked(true);
        assert_eq!(
            policy.decide(
                Observation::new(WifiState::Ready, false, PortalState::NeedsLogin),
                0,
                true,
                true,
                true
            ),
            ActionKind::None
        );
        assert_eq!(policy.decision_reason(), "AuthenticationPaused");

        let mut policy = RecoveryPolicy::new();
        policy.restore_disconnect_budget(
            DisconnectBudget::new(1, DisconnectRequestState::RequestPending, Some(0)),
            0,
        );
        let observation = Observation::new(WifiState::Ready, false, PortalState::Unknown);
        assert_eq!(
            policy.decide(observation, 0, true, true, true),
            ActionKind::None
        );
        assert_eq!(policy.decision_reason(), "DisconnectCompletionUnconfirmed");
    }

    #[test]
    fn 条件映射与在线重置() {
        assert_eq!(
            Observation::new(WifiState::Ready, false, PortalState::LoggedIn).condition(),
            NetworkCondition::AuthenticatedOffline
        );
        assert_eq!(
            Observation::new(WifiState::OtherProfile, true, PortalState::Unknown).condition(),
            NetworkCondition::Online
        );
        let mut policy = RecoveryPolicy::new();
        policy.restore_disconnect_budget(
            DisconnectBudget::new(2, DisconnectRequestState::Completed, Some(1)),
            0,
        );
        let online = Observation::new(WifiState::Ready, true, PortalState::Unknown);
        policy.decide(online, 0, true, true, true);
        assert_eq!(policy.disconnect_attempts(), 2);
        policy.decide(online, 60_000, true, true, true);
        assert_eq!(policy.disconnect_attempts(), 0);
    }
}
