using NBitcoin;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Wallets;

public class LockTimeSelectorTests
{
	[Fact]
	public void GetLockTimeBasedOnDistributionTest()
	{
		var lockTimeSelector = new LockTimeSelector(Random.Shared);

		uint tipHeight = 600_000;
		LockTime lockTime = lockTimeSelector.GetLockTimeBasedOnDistribution(tipHeight);

		if (lockTime.Value == 0)
		{
			Assert.Equal(LockTime.Zero, lockTime);
		}
		else
		{
			Assert.InRange(lockTime.Value, tipHeight - 99, tipHeight);
		}
	}

	[Theory]
	[InlineData(0u, 0.5, 0u)]
	[InlineData(109u, 0.95, 109u)]
	[InlineData(0u, 0.978, 0u)]
	[InlineData(109u, 0.978, 109u)]
	[InlineData(600_000u, 0.978, 600_000u)]
	[InlineData(0u, 0.99, 0u)]
	[InlineData(1u, 0.99, 0u)]
	[InlineData(600_000u, 0.99, 599_901u)]
	public void EveryDistributionBranchIsFinalAtTheCurrentTip(uint tipHeight, double sample, uint expected)
	{
		var selector = new LockTimeSelector(new FixedRandom(sample));

		var lockTime = selector.GetLockTimeBasedOnDistribution(tipHeight);

		Assert.Equal(expected, lockTime.Value);
		Assert.InRange(lockTime.Value, 0u, tipHeight);
	}

	private sealed class FixedRandom(double sample) : Random
	{
		public override double NextDouble() => sample;
		public override int Next(int minValue, int maxValue) => maxValue - 1;
	}
}
