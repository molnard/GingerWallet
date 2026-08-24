using System.Diagnostics;
using WalletWasabi.Logging;
using WalletWasabi.Microservices;

namespace WalletWasabi.Helpers.PowerSaving;

/// <summary>
/// Inhibitor based on <c>caffeinate</c> command.
/// </summary>
public class MacOsInhibitorTask : BaseInhibitorTask
{
	private MacOsInhibitorTask(TimeSpan period, string reason, ProcessAsync process)
		: base(period, reason, process)
	{
	}

	public static MacOsInhibitorTask Create(TimeSpan basePeriod, string reason)
	{
		ProcessStartInfo startInfo = CreateProcessStartInfo();

		Logger.LogTrace($"Command to invoke: {startInfo.FileName} {startInfo.Arguments}");

		ProcessAsync process = new(startInfo);
		process.Start();
		MacOsInhibitorTask task = new(basePeriod, reason, process);

		return task;
	}

	internal static ProcessStartInfo CreateProcessStartInfo()
	{
		return GetProcessStartInfo("caffeinate", $"-i -w {Environment.ProcessId}");
	}
}
