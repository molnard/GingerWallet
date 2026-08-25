using System;
using System.Diagnostics;
using WalletWasabi.Helpers.PowerSaving;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Helpers.PowerSaving;

public class MacOsInhibitorTaskTests
{
	[Fact]
	public void CaffeinateWatchesWalletProcess()
	{
		ProcessStartInfo startInfo = MacOsInhibitorTask.CreateProcessStartInfo();

		Assert.Equal("caffeinate", startInfo.FileName);
		Assert.Equal($"-i -w {Environment.ProcessId}", startInfo.Arguments);
		Assert.False(startInfo.UseShellExecute);
	}
}
