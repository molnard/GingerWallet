using WalletWasabi.SecretHunt;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.SecretHunt;

public class SecretHuntResultsTests
{
	[Fact]
	public void SecretHuntIsDisabledByDefault()
	{
		var results = new SecretHuntResults();

		Assert.False(results.Enabled);
	}
}
