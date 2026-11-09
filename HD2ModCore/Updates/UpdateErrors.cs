namespace HD2ModCore.Updates;

public enum UpdateErrorCode
{
	Network, RateLimited, Http, BadResponse, NoPackage, NoDigest, DigestMismatch, Busy,
	NotInstallable, NotReady, NothingNew, Cancelled, Failed, DiskSpace, BadPackage,
	/// <summary>替换脚本没能确认已停止：本次运行内不能再安装，需要重启管理器。</summary>
	HelperUnconfirmed,
}

public sealed class UpdateException : Exception
{
	public UpdateErrorCode Code { get; }

	public UpdateException(UpdateErrorCode code, string message, Exception? inner = null) : base(message, inner) => Code = code;
}

public enum DigestSource
{
	/// <summary>官方地址拿到（或与镜像一致）。</summary>
	Github,

	/// <summary>走镜像拿到。</summary>
	Mirror,

	/// <summary>
	/// SHA256SUMS 两条路都取不到，只能信 API 返回的资产摘要 —— 校验强度不变的降级：
	/// 下载后仍然严格比对 SHA-256，只是少了 SHA256SUMS 这层交叉校验。界面/日志据此说明口径。
	/// </summary>
	ApiDigestOnly,
}

/// <summary>是否值得换镜像重试：网站连不上或被限流；其他错误与网络路径无关。</summary>
public static class UpdateErrors
{
	public static bool MirrorHelps(UpdateErrorCode code) => code is UpdateErrorCode.Network or UpdateErrorCode.RateLimited or UpdateErrorCode.Http;
}
