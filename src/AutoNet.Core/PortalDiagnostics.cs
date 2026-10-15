namespace AutoNet.Core;

public enum PortalStage { Unknown, Status, GatewayRedirect, HomePage, ClientIpv4, Configuration, AuthenticationMode, Submission, Complete }
public enum PortalFailure { None, InvalidTarget, AlreadyAuthenticated, StatusUnknown, HttpStatus, MissingClientIpv4, MalformedConfiguration, MissingFlag, InvalidFlagType, UnsupportedFlag, DuplicateFlag, InvalidResponse, Timeout, Transport, Tls, CredentialsRejected, InteractionRequired, LoginRejected, MissingGatewayParameters, BindingRejected, SafetyPersistence }

/// <summary>
/// ADR-0005 structural failure set. A preflight failure in this set means the portal's
/// response shape changed — not that credentials are wrong and not that the network is
/// down. The service aggregates it into the PortalLayoutChanged signal (journal event,
/// status flag, one-shot notification) instead of a generic dynamic parser, which would
/// put untestable guesswork into the production authentication path.
/// Mapping of the ADR's enumeration: MissingFlag/UnsupportedFlag (+ the type and
/// duplicate variants of the same loadConfig contract) and MalformedConfiguration cover
/// the configuration shape; InvalidResponse covers unparseable bodies, including a
/// chkstatus JSONP that lost its result field; MissingGatewayParameters covers a gateway
/// redirect that no longer carries the login context. StatusUnknown is deliberately not
/// structural: a recognised shape with an unrecognised state value is not a layout change.
/// </summary>
public static class PortalLayoutSignal
{
    public static bool IsStructural(PortalFailure failure) => failure is
        PortalFailure.MissingFlag or PortalFailure.InvalidFlagType or PortalFailure.UnsupportedFlag or
        PortalFailure.DuplicateFlag or PortalFailure.MalformedConfiguration or PortalFailure.InvalidResponse or
        PortalFailure.MissingGatewayParameters;
}

public sealed record PortalAttempt(LoginResult Result, PortalStage Stage, PortalFailure Failure, int? HttpStatus = null, string? Field = null, bool CredentialRequestStarted = false)
{
    public PortalState ObservedState { get; init; } = PortalState.Unknown;
    public IReadOnlyDictionary<string, string>? Flags { get; init; }
    public bool RetryPermitted { get; init; }
    public PortalEvidence? Evidence { get; init; }
    public DiagnosticError? Error { get; init; }
    public string SafeSummary => $"{StageText}：{FailureText} [{Stage}/{Failure}]" +
        (HttpStatus is { } status ? $" HTTP {status}" : "") +
        (Field is "en_md5" or "password_cut" or "enable_r3" ? " 字段 " + Field : "") +
        (CredentialRequestStarted ? "；凭据请求已发起" : "；未发送凭据");

    private string StageText => Stage switch
    {
        PortalStage.Status => "认证状态查询", PortalStage.GatewayRedirect => "网关重定向读取",
        PortalStage.HomePage => "登录首页读取", PortalStage.ClientIpv4 => "客户端 IPv4 提取",
        PortalStage.Configuration => "802 配置读取", PortalStage.AuthenticationMode => "认证模式校验", PortalStage.Submission => "认证提交",
        PortalStage.Complete => "认证前置检查", _ => "认证目标校验"
    };
    private string FailureText => Failure switch
    {
        PortalFailure.None => Result == LoginResult.Accepted && CredentialRequestStarted ? "接口接受，仍需验证外网" : "通过",
        PortalFailure.InvalidTarget => "目标不受支持", PortalFailure.AlreadyAuthenticated => "已认证，不提交密码",
        PortalFailure.StatusUnknown => "状态未知", PortalFailure.HttpStatus => "HTTP 状态异常",
        PortalFailure.MissingClientIpv4 => "未找到受支持的客户端 IPv4", PortalFailure.MalformedConfiguration => "配置结构无法确认",
        PortalFailure.MissingFlag => "缺少必需字段", PortalFailure.InvalidFlagType => "字段类型不受支持",
        PortalFailure.UnsupportedFlag => "字段值不受支持", PortalFailure.DuplicateFlag => "字段重复",
        PortalFailure.InvalidResponse => "响应无法解析", PortalFailure.Timeout => "请求超时", PortalFailure.Transport => "网络请求失败",
        PortalFailure.Tls => "TLS 校验或握手失败", PortalFailure.CredentialsRejected => "服务器明确拒绝凭据",
        PortalFailure.InteractionRequired => "需要人工验证",
        PortalFailure.BindingRejected => "网关或设备绑定被拒绝；请核实 VLAN、MAC/AC 参数，不能据此判断密码错误",
        PortalFailure.SafetyPersistence => "认证提交授权未能持久保存，停止提交并保留安全暂停",
        PortalFailure.MissingGatewayParameters => "网关未返回登录所需参数", _ => "接口未接受认证"
    };
}
