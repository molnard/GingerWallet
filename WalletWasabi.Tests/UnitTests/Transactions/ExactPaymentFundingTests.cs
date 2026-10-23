using NBitcoin;
using System.Linq;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Exceptions;
using WalletWasabi.Models;
using WalletWasabi.Stores;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Transactions;

public class ExactPaymentFundingTests
{
	[Theory]
	[InlineData(ScriptPubKeyType.Segwit, 12)]
	[InlineData(ScriptPubKeyType.TaprootBIP86, 12)]
	[InlineData(ScriptPubKeyType.Segwit, 0.5)]
	[InlineData(ScriptPubKeyType.Segwit, 0.125)]
	public async Task ExactAmountQuotedWithoutChangeCanBeBuiltAsFixedPaymentAsync(ScriptPubKeyType inputType, double rate)
	{
		await using var store = new AllTransactionStore(SqliteStorageHelper.InMemoryDatabase, Network.RegTest);
		await store.InitializeAsync();
		var keys = KeyManager.CreateNew(out _, "", Network.RegTest);
		var key = keys.GetNextReceiveKey("funding", inputType);
		var fundingTx = Network.RegTest.CreateTransaction();
		fundingTx.Inputs.Add(new OutPoint(RandomUtils.GetUInt256(), 0));
		fundingTx.Outputs.Add(Money.Coins(0.011m), key.PubKey.GetScriptPubKey(inputType));
		var funding = new SmartTransaction(fundingTx, new Height(100));
		store.AddOrUpdate(funding);
		var coin = new SmartCoin(funding, 0, key);
		var factory = new TransactionFactory(Network.RegTest, keys, new CoinsView([coin]), store);
		using var recipient = new Key();
		var destination = recipient.PubKey.WitHash.ScriptPubKey;
		var feeRate = new FeeRate((decimal)rate);
		var quoted = factory.BuildTransaction(new TransactionParameters(
			new PaymentIntent(destination, MoneyRequest.CreateAllRemaining(subtractFee: true)),
			feeRate, false, false, [coin.Outpoint], false, false));
		var quotedAmount = Assert.Single(quoted.Transaction.Transaction.Outputs).Value;
		var payment = new PaymentIntent(destination, quotedAmount);
		var parameters = new TransactionParameters(payment, feeRate, false, false, [coin.Outpoint], true, false);

		var result = factory.BuildTransaction(parameters, () => new LockTime(50));

		Assert.Equal(coin.Outpoint, Assert.Single(result.Transaction.Transaction.Inputs).PrevOut);
		Assert.Equal(quotedAmount, Assert.Single(result.Transaction.Transaction.Outputs).Value);
		Assert.Equal(quoted.Fee, result.Fee);
		Assert.Equal(new LockTime(50), result.Transaction.Transaction.LockTime);
		Assert.True(result.Fee.Satoshi >= Math.Ceiling(feeRate.SatoshiPerByte * result.Transaction.Transaction.GetVirtualSize()));
		Assert.True(result.Psbt.IsAllFinalized());
	}

	[Theory]
	[InlineData(12, 1_098_681)]
	[InlineData(0.125, 1_099_987)]
	public async Task BelowRequestedFeeStillFailsAsync(double rate, long amount)
	{
		await using var store = new AllTransactionStore(SqliteStorageHelper.InMemoryDatabase, Network.RegTest);
		await store.InitializeAsync();
		var keys = KeyManager.CreateNew(out _, "", Network.RegTest);
		var key = keys.GetNextReceiveKey("funding");
		var fundingTx = Network.RegTest.CreateTransaction();
		fundingTx.Inputs.Add(new OutPoint(RandomUtils.GetUInt256(), 0));
		fundingTx.Outputs.Add(Money.Satoshis(1_100_000), key.P2wpkhScript);
		var funding = new SmartTransaction(fundingTx, new Height(100));
		store.AddOrUpdate(funding);
		var coin = new SmartCoin(funding, 0, key);
		var factory = new TransactionFactory(Network.RegTest, keys, new CoinsView([coin]), store);
		using var recipient = new Key();
		var parameters = new TransactionParameters(new PaymentIntent(recipient.PubKey.WitHash.ScriptPubKey, Money.Satoshis(amount)),
			new FeeRate((decimal)rate), false, false, [coin.Outpoint], false, false);

		Assert.Throws<InsufficientBalanceException>(() => factory.BuildTransaction(parameters));
	}
}
