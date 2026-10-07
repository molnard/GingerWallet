using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NBitcoin;
using NBitcoin.Protocol;
using WalletWasabi.Blockchain.TransactionBroadcasting;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Daemon.FeeRateProviders;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.TestCommon;
using WalletWasabi.Tor.Http;
using Xunit;

namespace WalletWasabi.Tests.UnitTests;

public class SubSatoshiFeeTests
{
	[Theory]
	[InlineData(0.1, false, false)]
	[InlineData(0.101, true, false)]
	[InlineData(0.125, false, true)]
	[InlineData(0.333, true, true)]
	[InlineData(0.999, false, false)]
	public void SignedAndWatchOnlyTransactionsCoverFractionalFees(decimal rate, bool subtractFee, bool watchOnly)
	{
		var factory = ServiceFactory.CreateTransactionFactory(TestRandom.Get(), [("Alice", 0, 0.01m, true, 1)], watchOnly);
		using var key = new Key();
		var destination = key.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.Main);
		var amount = Money.Coins(0.005m);
		var payment = new PaymentIntent(destination, MoneyRequest.Create(amount, subtractFee));
		var parameters = TransactionParametersBuilder.CreateDefault().SetPayment(payment).SetFeeRate(rate).Build();

		var result = factory.BuildTransaction(parameters);

		Assert.True(result.Psbt.TryGetVirtualSize(out var size));
		Assert.True(result.Fee.Satoshi >= Math.Ceiling(rate * size));
		Assert.Equal(amount - (subtractFee ? result.Fee : Money.Zero), Assert.Single(result.OuterWalletOutputs).Amount);
		Assert.Equal(result.SpentCoins.Sum(x => x.Amount.Satoshi) - result.Transaction.Transaction.Outputs.Sum(x => x.Value.Satoshi), result.Fee.Satoshi);
		Assert.Equal(!watchOnly, result.Signed);
		if (!watchOnly)
		{
			Assert.True(result.Psbt.IsAllFinalized());
		}
	}

	[Fact]
	public void MinimumStrategyRejectsBelowRelayFloor()
	{
		Assert.True(FeeStrategy.CreateFromFeeRate(0.1m).TryGetFeeRate(out var accepted));
		Assert.Equal(0.1m, accepted!.SatoshiPerByte);
		Assert.Throws<ArgumentOutOfRangeException>(() => FeeStrategy.CreateFromFeeRate(0.099m));
	}

	[Theory]
	[InlineData(NodeState.HandShaked, true, 0.1, 0.1, true)]
	[InlineData(NodeState.HandShaked, true, 0.1, 0.099, false)]
	[InlineData(NodeState.HandShaked, true, 1.0, 0.1, false)]
	[InlineData(NodeState.HandShaked, true, null, 0.1, false)]
	[InlineData(NodeState.HandShaked, true, null, 1.0, true)]
	[InlineData(NodeState.HandShaked, true, 0.1, null, true)]
	[InlineData(NodeState.HandShaked, false, 0.1, 0.1, false)]
	[InlineData(NodeState.Connected, true, 0.1, 0.1, false)]
	[InlineData(NodeState.Offline, true, 0.1, 0.1, false)]
	public void RelayCandidatesRespectHandshakePreferenceAndFeeFloor(NodeState state, bool relay, double? floor, double? rate, bool expected)
	{
		Assert.Equal(expected, TransactionBroadcaster.CanRelayTransaction(state, relay,
			floor is { } value ? new FeeRate((decimal)value) : null, rate is { } requested ? new FeeRate((decimal)requested) : null));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task MempoolUsesPreciseFeesWithLegacyFallbackAsync(bool legacy)
	{
		const string json = "{\"fastestFee\":0.125,\"halfHourFee\":0.111,\"hourFee\":0.101,\"economyFee\":0.1}";
		using var preciseResponse = new HttpResponseMessage(legacy ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent(json) };
		using var legacyResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
		var http = new Mock<IHttpClient>(MockBehavior.Strict);
		http.Setup(x => x.SendAsync(HttpMethod.Get, "fees/precise", null, It.IsAny<CancellationToken>())).ReturnsAsync(preciseResponse);
		if (legacy)
		{
			http.Setup(x => x.SendAsync(HttpMethod.Get, "fees/recommended", null, It.IsAny<CancellationToken>())).ReturnsAsync(legacyResponse);
		}

		var result = await new MempoolSpaceFeeRateProvider(http.Object).GetFeeRatesAsync(CancellationToken.None);

		Assert.Equal(0.125m, result.Estimations[2].SatoshiPerByte);
		Assert.Equal(0.1m, result.Estimations[72].SatoshiPerByte);
		http.Verify(x => x.SendAsync(HttpMethod.Get, "fees/recommended", null, It.IsAny<CancellationToken>()), legacy ? Times.Once() : Times.Never());
	}
}
