namespace AntDeployWeb.Services.Remote
{
    /// <summary>
    /// 远程命令 / 上传的执行结果。
    /// <para><see cref="Success"/> 表示业务是否成功（命令退出码 0 且无通道级错误）；
    /// <see cref="Message"/> 承载连接失败 / 认证失败 / 通道不支持等“通道级”原因，供 UI ✗ 展示与排错。</para>
    /// </summary>
    public class RemoteResult
    {
        public bool Success { get; set; }

        /// <summary>远程命令退出码（上传 / 连接测试无退出码时为 0）。</summary>
        public int ExitCode { get; set; }

        /// <summary>标准输出（已流式回传 onLog，此处保留完整文本供入库 / 排错）。</summary>
        public string Output { get; set; } = string.Empty;

        /// <summary>标准错误 / PowerShell 错误记录。</summary>
        public string Error { get; set; } = string.Empty;

        /// <summary>通道级说明（连接失败、认证失败、通道不支持等）。</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>构造一个通道级失败结果（如连接 / 认证 / 不支持）。</summary>
        public static RemoteResult Fail(string message, string? error = null) => new RemoteResult
        {
            Success = false,
            ExitCode = -1,
            Message = message,
            Error = error ?? string.Empty
        };

        /// <summary>失败原因优先取 Message，其次 Error，最后 Output（供逐台 ✗ 简短展示）。</summary>
        public string Reason =>
            !string.IsNullOrWhiteSpace(Message) ? Message
            : !string.IsNullOrWhiteSpace(Error) ? Error
            : !string.IsNullOrWhiteSpace(Output) ? Output
            : "未知错误";
    }

    /// <summary>
    /// 远程传输通道抽象：SSH / WinRM 统一接口，供 <c>AgentInstaller</c> 编排调用。
    /// 每个实例对应一台目标服务器的一种通道；实现负责连接生命周期（<see cref="IDisposable"/>）。
    /// </summary>
    public interface IRemoteTransport : IDisposable
    {
        /// <summary>通道名称（ssh / winrm），用于日志与错误提示。</summary>
        string ChannelName { get; }

        /// <summary>目标展示（host:port/channel）。</summary>
        string Display { get; }

        /// <summary>
        /// 测试连接：成功返回 true；失败将原因写入 <paramref name="onLog"/> 并返回 false。
        /// 连接测试应快速失败（网络 / 认证 / 通道不支持）。
        /// </summary>
        Task<bool> TestConnectAsync(Action<string>? onLog, CancellationToken ct = default);

        /// <summary>
        /// 执行远程脚本 / 命令，stdout / stderr 流式回传 <paramref name="onLog"/>。
        /// </summary>
        Task<RemoteResult> RunAsync(string script, Action<string>? onLog, CancellationToken ct = default);

        /// <summary>
        /// 上传本地文件或目录到远程<b>目标目录</b> <paramref name="remotePath"/>：
        /// 文件源 → 落入该目录并保留文件名；目录源 → 其内容合并进该目录（递归）。
        /// 成功返回 true，进度 / 结果写入 <paramref name="onLog"/>。
        /// </summary>
        Task<bool> UploadAsync(string localPath, string remotePath, Action<string>? onLog, CancellationToken ct = default);
    }
}
