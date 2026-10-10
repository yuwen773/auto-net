namespace AutoNet.Core;

public enum PortalStage { Unknown, Status, GatewayRedirect, HomePage, ClientIpv4, Configuration, AuthenticationMode, Submission, Complete }
public enum PortalFailure { None, InvalidTarget, AlreadyAuthenticated, StatusUnknown, HttpStatus, MissingClientIpv4, MalformedConfiguration, MissingFlag, InvalidFlagType, UnsupportedFlag, DuplicateFlag, InvalidResponse, Timeout, Transport, Tls, CredentialsRejected, InteractionRequired, LoginRejected, MissingGatewayParameters }

public sealed record PortalAttempt(LoginResult Result, PortalStage Stage, PortalFailure Failure, int? HttpStatus = null, string? Field = null, bool CredentialRequestStarted = false)
{
    public PortalState ObservedState { get; init; } = PortalState.Unknown;
    public IReadOnlyDictionary<string, string>? Flags { get; init; }
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
        PortalFailure.MissingGatewayParameters => "网关未返回登录所需参数", _ => "接口未接受认证"
    };
}
