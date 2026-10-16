using System.Net;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 只读门户诊断夹具（迁移工单 #5 的 QA 与回归共用）。
/// 只应答只读前置检查的三段请求——状态查询、网关重定向、802 配置——并记录全部请求路径。
/// 它存在的唯一目的是让「解除认证暂停」的复核路径可在无网络、无凭据的条件下被反复执行：
/// 任何携带 <c>user_password</c> 的请求都会被标记出来（故事 23 交叉的机器可核验证据）。
/// 全部取值都是合成值，不含任何真实校园网身份。
/// </summary>
internal sealed class ReadOnlyPortalFixture : HttpMessageHandler
{
    /// <summary>网关重定向：合成身份，形状才是解析器关心的东西。</summary>
    internal const string GatewayRedirect =
        "https://login.csust.edu.cn/?wlanuserip=10.0.0.1&wlan_ac_name=TEST-AP-1&wlanacip=192.168.100.254" +
        "&wlanusermac=001122334455667700112233445566778899aabbccddeeff";

    /// <summary>缺少 MAC/AC 的网关重定向：只读诊断据此受控失败，模拟前置复核不通过。</summary>
    private const string GatewayRedirectWithoutDeviceBinding =
        "https://login.csust.edu.cn/?wlanuserip=10.0.0.1";

    /// <summary>出现在请求记录里的凭据请求标记：断言必须保证它永不出现。</summary>
    internal const string CredentialBearingRequest = "!!CREDENTIAL-BEARING-REQUEST";

    /// <summary>已发出的请求路径，按发生顺序。</summary>
    public List<string> RequestPaths { get; } = [];

    /// <summary>为 true 时让只读诊断受控失败（网关缺少设备绑定参数）。</summary>
    public bool RecheckFails { get; init; }

    /// <summary>诊断请求完成时触发：QA 用它制造「诊断期间事实变化」的修订核对场景。</summary>
    public Action? OnDiagnosed { get; init; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        RequestPaths.Add(uri.AbsolutePath);
        if (uri.Query.Contains("user_password", StringComparison.Ordinal)) RequestPaths.Add(CredentialBearingRequest);

        if (uri.AbsolutePath == "/connecttest.txt")
        {
            var redirected = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent("") };
            redirected.Headers.Location = new Uri(RecheckFails ? GatewayRedirectWithoutDeviceBinding : GatewayRedirect);
            return Task.FromResult(redirected);
        }

        // 诊断链的收尾一段：读完配置即视为前置检查走完。
        if (uri.AbsolutePath == "/eportal/portal/page/loadConfig") OnDiagnosed?.Invoke();
        var body = uri.AbsolutePath switch
        {
            "/drcom/chkstatus" => "{\"result\":0,\"v4ip\":\"10.0.0.1\"}",
            "/eportal/portal/page/loadConfig" => "{\"data\":{\"en_md5\":0,\"password_cut\":0,\"enable_r3\":0}}",
            _ => ""
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
