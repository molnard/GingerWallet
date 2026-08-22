using WalletWasabi.Rpc;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Rpc;

public class JsonRpcServerConfigurationTests
{
	private static readonly string[] Prefixes = ["http://localhost:37128/"];

	[Theory]
	[InlineData("", "password")]
	[InlineData("user", "")]
	[InlineData(" ", "password")]
	[InlineData("user", " ")]
	public void EnabledServerRequiresCompleteCredentials(string user, string password)
	{
		var exception = Assert.Throws<ArgumentException>(() => new JsonRpcServerConfiguration(true, user, password, Prefixes));

		Assert.Contains("requires both", exception.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void DisabledServerAllowsMissingCredentials()
	{
		var configuration = new JsonRpcServerConfiguration(false, "", "", Prefixes);

		Assert.False(configuration.HasCredentials);
	}

	[Fact]
	public void EnabledServerAllowsCompleteCredentials()
	{
		var configuration = new JsonRpcServerConfiguration(true, "user", "password", Prefixes);

		Assert.True(configuration.HasCredentials);
	}
}
