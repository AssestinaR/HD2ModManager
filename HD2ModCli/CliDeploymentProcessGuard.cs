using System.Diagnostics;

namespace HD2ModCli;

/// <summary>
/// Deployment writes are refused whenever the game process cannot be checked
/// reliably. Planning remains read-only, so callers may surface these facts as
/// blockers without needing a writable target.
/// </summary>
internal static class CliDeploymentProcessGuard
{
	public static CliDeploymentProcessCheck Inspect()
	{
		try
		{
			var processIds = Process.GetProcessesByName("helldivers2")
				.Select(process =>
				{
					try { return process.Id; }
					finally { process.Dispose(); }
				})
				.OrderBy(id => id)
				.ToArray();
			return new CliDeploymentProcessCheck(true, processIds, null);
		}
		catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
		{
			return new CliDeploymentProcessCheck(false, Array.Empty<int>(), exception.Message);
		}
	}
}

internal sealed record CliDeploymentProcessCheck(
	bool IsReliable,
	IReadOnlyList<int> ProcessIds,
	string? Error)
{
	public bool IsGameRunning => ProcessIds.Count > 0;
}
