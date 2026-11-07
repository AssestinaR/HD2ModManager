namespace HD2ModCore.Updates;

public enum UpdateErrorCode
{
	Network, RateLimited, Http, BadResponse, NoPackage, NoDigest, DigestMismatch, Busy,
	NotInstallable, NotReady, NothingNew, Cancelled, Failed, DiskSpace, BadPackage,
}

public sealed class UpdateException : Exception
{
	public UpdateErrorCode Code { get; }

	public UpdateException(UpdateErrorCode code, string message, Exception? inner = null) : base(message, inner) => Code = code;
}

public enum DigestSource { Github, Mirror }

/// <summary>是否值得换镜像重试：网站连不上或被限流；其他错误与网络路径无关。</summary>
public static class UpdateErrors
{
	public static bool MirrorHelps(UpdateErrorCode code) => code is UpdateErrorCode.Network or UpdateErrorCode.RateLimited or UpdateErrorCode.Http;
}
