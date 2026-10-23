using NBitcoin;
using System.Collections.Generic;
using System.Linq;
using WalletWasabi.Blockchain.Analysis;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Mempool;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Extensions;
using WalletWasabi.Models;

namespace WalletWasabi.Blockchain.TransactionProcessing;

public class TransactionProcessor
{
	public TransactionProcessor(
		AllTransactionStore transactionStore,
		MempoolService? mempoolService,
		KeyManager keyManager,
		Money dustThreshold)
	{
		TransactionStore = transactionStore;
		MempoolService = mempoolService;
		KeyManager = keyManager;
		DustThreshold = dustThreshold;
		Coins = new();
		BlockchainAnalyzer = new();
	}

	public event EventHandler<ProcessedResult>? WalletRelevantTransactionProcessed;
	/// <summary>Persists invalidated transactions before a confirmed conflict changes active state.</summary>
	public event Action<SmartTransaction, IReadOnlyList<SmartTransaction>>? ConfirmedConflictDetected;
	public event Action<SmartTransaction>? UnconfirmedTransactionDetected;
	public Func<uint256, bool>? IsInvalidatedTransaction { get; set; }

	/// <remarks>Intentionally, <c>static</c> to avoid modifying smart transactions from multiple threads.</remarks>
	public static object Lock { get; } = new();

	public AllTransactionStore TransactionStore { get; }
	private HashSet<uint256> Aware { get; } = new();

	public KeyManager KeyManager { get; }

	public CoinsRegistry Coins { get; }
	private BlockchainAnalyzer BlockchainAnalyzer { get; }
	public Money DustThreshold { get; }
	private MempoolService? MempoolService { get; }

	public IEnumerable<ProcessedResult> Process(IEnumerable<SmartTransaction> txs)
	{
		var rets = new List<ProcessedResult>();

		lock (Lock)
		{
			var transactions = txs.ToArray();
			// Full block scans can contain thousands of foreign transactions. The
			// pending ancestry is unchanged by an all-confirmed batch, so index it once.
			var conflicts = transactions.Length != 0 && transactions.All(transaction => transaction.Confirmed)
				? BuildConfirmedConflictGraph()
				: null;
			foreach (var tx in transactions)
			{
				rets.Add(ProcessAndRememberNoLock(tx, remember: false, conflicts));
			}
		}

		foreach (var ret in rets.Where(x => x.IsNews))
		{
			WalletRelevantTransactionProcessed?.Invoke(this, ret);
		}

		return rets;
	}

	public IEnumerable<ProcessedResult> Process(params SmartTransaction[] txs)
		=> Process(txs as IEnumerable<SmartTransaction>);

	/// <summary>
	/// Was the transaction already processed by the transaction processor?
	/// </summary>
	public bool IsAware(uint256 tx)
	{
		lock (Lock)
		{
			return Aware.Contains(tx);
		}
	}

	public void ForgetTransaction(uint256 tx)
	{
		lock (Lock)
		{
			Aware.Remove(tx);
			MempoolService?.ForgetTransaction(tx);
		}
	}

	public ProcessedResult Process(SmartTransaction tx)
	{
		ProcessedResult ret;
		lock (Lock)
		{
			ret = ProcessAndRememberNoLock(tx);
		}
		if (ret.IsNews)
		{
			WalletRelevantTransactionProcessed?.Invoke(this, ret);
		}
		return ret;
	}

	private ProcessedResult ProcessAndRememberNoLock(SmartTransaction tx, bool remember = true, ConfirmedConflictGraph? conflicts = null)
	{
		try
		{
			var result = ProcessNoLock(tx, conflicts);
			// Archived transactions must stay eligible for rediscovery after a reorg.
			if (remember && (tx.Confirmed || IsInvalidatedTransaction?.Invoke(tx.GetHash()) is not true))
			{
				Aware.Add(tx.GetHash());
			}
			return result;
		}
		catch
		{
			// A failed persistence write must allow the same P2P transaction to retry.
			ForgetTransaction(tx.GetHash());
			throw;
		}
	}

	private void RecordUnconfirmedBeforeMutation(SmartTransaction tx)
	{
		if (!tx.Confirmed && (Coins.GetMyInputs(tx).Count != 0
			|| tx.Transaction.Outputs.Any(output => KeyManager.TryGetKeyForScriptPubKey(output.ScriptPubKey, out _))))
		{
			UnconfirmedTransactionDetected?.Invoke(tx);
		}
	}

	private ProcessedResult ProcessNoLock(SmartTransaction tx, ConfirmedConflictGraph? conflicts)
	{
		var result = new ProcessedResult(tx);
		if (!tx.Confirmed && IsInvalidatedTransaction?.Invoke(tx.GetHash()) is true)
		{
			Aware.Remove(tx.GetHash());
			return result;
		}

		// A confirmed non-witness transaction can invalidate an incoming witness payment
		// by spending its external inputs, even when it has no output for this wallet.
		if (!tx.Confirmed && !tx.Transaction.SegWitInvolved())
		{
			return result;
		}

		uint256 txId = tx.GetHash();

		// If we already have the transaction, then let's work on that.
		if (MempoolService?.TryGetFromBroadcastStore(txId, out var foundEntry) is true)
		{
			// If we already have the transaction in the broadcast store, then let's work on that.
			foundEntry.Transaction.TryUpdate(tx);
			tx = foundEntry.Transaction;
			result = new ProcessedResult(tx);
		}

		if (TransactionStore.TryGetActiveTransaction(txId, out var foundTx))
		{
			foundTx.TryUpdate(tx);
			tx = foundTx;
			result = new ProcessedResult(tx);
		}

		if (!tx.Transaction.IsCoinBase && !Coins.IsKnown(txId)) // Transactions we already have and processed would be "double spends" but they shouldn't.
		{
			var doubleSpentSpenders = new List<SmartCoin>();
			var doubleSpentCoins = new List<SmartCoin>();
			foreach (var txIn in tx.Transaction.Inputs)
			{
				if (Coins.TryGetCoinsByInputPrevOut(txIn.PrevOut, out var coins))
				{
					doubleSpentSpenders.AddRange(coins);
				}

				if (Coins.TryGetByOutPoint(txIn.PrevOut, out var coin) && coin.IsSpent())
				{
					doubleSpentCoins.Add(coin);
				}
			}

			var doubleSpentTransactions = doubleSpentCoins.Select(x => x.SpenderTransaction!).Concat(doubleSpentSpenders.Select(x => x.Transaction))
				.Where(transaction => transaction.GetHash() != txId).ToHashSet();
			if (tx.Confirmed)
			{
				conflicts ??= BuildConfirmedConflictGraph();
				foreach (var input in tx.Transaction.Inputs)
				{
					if (conflicts.RootsByInput.TryGetValue(input.PrevOut, out var roots))
					{
						doubleSpentTransactions.UnionWith(roots.Where(root => !root.Confirmed && root.GetHash() != txId));
					}
				}
			}

			if (doubleSpentTransactions.Count > 0)
			{
				tx.SetReplacement();
			}
			RecordUnconfirmedBeforeMutation(tx);

			if (tx.Height == Height.Mempool)
			{
				// if the received transaction is spending at least one input already
				// spent by a previous unconfirmed transaction then it is not considered a double
				// spending transaction but a replacement transaction.
				var isReplacementTx = doubleSpentSpenders.Count != 0;
				if (isReplacementTx)
				{
					// Undo the replaced transaction by removing the coins it created (if other coin
					// spends it, remove that too and so on) and restoring those that it replaced.
					// After undoing the replaced transaction it will process the replacement transaction.
					var replacedTxId = doubleSpentSpenders.First().TransactionId;
					var (replaced, restored) = Coins.Undo(replacedTxId);

					result.ReplacedCoins.AddRange(replaced);
					result.RestoredCoins.AddRange(restored);
				}
			}
			else // new confirmation always enjoys priority
			{
				InvalidateConfirmedConflicts(tx, doubleSpentTransactions, result, conflicts!);
			}

			// Recursively double spent transactions could be here.
			foreach (var doubleSpentTx in result.ReplacedCoins.Select(coin => coin.Transaction))
			{
				doubleSpentTransactions.Add(doubleSpentTx);
			}

			foreach (var replacedTransactionId in doubleSpentTransactions.Select(x => x.GetHash()))
			{
				TransactionStore.MempoolStore.TryRemove(replacedTransactionId, out _);
			}
		}
		else
		{
			RecordUnconfirmedBeforeMutation(tx);
		}

		IReadOnlyList<SmartCoin> myInputs = Coins.GetMyInputs(tx);
		List<(uint Index, TxOut Output, HdPubKey PubKey)> myOutputs = new();
		for (var i = 0U; i < tx.Transaction.Outputs.Count; i++)
		{
			var output = tx.Transaction.Outputs[i];
			if (KeyManager.TryGetKeyForScriptPubKey(output.ScriptPubKey, out HdPubKey? foundKey))
			{
				myOutputs.Add(new(i, output, foundKey));
			}
		}

		bool coinJoin = KeyManager.Attributes.CoinJoinTransactions.Contains(txId);
		if (!coinJoin && !tx.Confirmed)
		{
			// We are allowed to do false negative here, but false positive is not allowed or we might falsly try to get exemption later from the coordinator
			int txInputCount = tx.Transaction.Inputs.Count, txOutputCount = tx.Transaction.Outputs.Count;
			if (txInputCount >= 21 && txOutputCount >= 15 && myOutputs.All(x => x.PubKey.IsInternal))
			{
				int myInputCount = myInputs.Count, myOutputCount = myOutputs.Count;
				if (txInputCount > myOutputCount + 4 && txOutputCount > myOutputCount + 4 && myInputCount + myOutputCount > 0)
				{
					coinJoin = true;
				}
			}
		}

		foreach (var (i, output, foundKey) in myOutputs)
		{
			if (!foundKey.IsInternal)
			{
				tx.Labels = LabelsArray.Merge(tx.Labels, foundKey.Labels);
			}

			var couldBeDustAttack = CanBeConsideredDustAttack(output, foundKey, myInputs.Any());
			KeyManager.SetKeyState(KeyState.Used, foundKey);
			if (couldBeDustAttack)
			{
				result.ReceivedDusts.Add(output);
				continue;
			}

			SmartCoin newCoin = new(tx, i, foundKey);
			newCoin.IsCoinJoinOutput = coinJoin;

			result.ReceivedCoins.Add(newCoin);

			// If we did not have it.
			if (Coins.TryAdd(newCoin))
			{
				result.NewlyReceivedCoins.Add(newCoin);
			}
			else // If we had this coin already.
			{
				if (newCoin.Height != Height.Mempool || coinJoin) // Update the height of this old coin we already had.
				{
					if (Coins.AsAllCoinsView().TryGetByOutPoint(new OutPoint(txId, i), out var oldCoin)) // Just to be sure, it is a concurrent collection.
					{
						if (newCoin.Height != Height.Mempool)
						{
							result.NewlyConfirmedReceivedCoins.Add(newCoin);
							oldCoin.Height = newCoin.Height;
						}
						if (coinJoin)
						{
							oldCoin.IsCoinJoinOutput = true;
						}
					}
				}
			}
		}

		// If spends any of our coin
		foreach (var coin in myInputs)
		{
			var alreadyKnown = coin.SpenderTransaction == tx;
			result.SpentCoins.Add(coin);
			Coins.Spend(coin, tx);
			MempoolService?.TrySpend(coin, tx);
			result.RestoredCoins.Remove(coin);

			if (!alreadyKnown)
			{
				result.NewlySpentCoins.Add(coin);
			}

			if (tx.Confirmed)
			{
				result.NewlyConfirmedSpentCoins.Add(coin);
			}
		}

		if (tx.Confirmed)
		{
			// Update for TurboSync - save spending height for internal keys if there is a spender tx and no more coins left on the key.
			SaveInternalKeysLatestSpendingHeight(tx.Height, myInputs.Select(x => x.HdPubKey).Where(x => x.IsInternal).Distinct());
		}

		if (tx.WalletInputs.Count != 0 || tx.WalletOutputs.Count != 0)
		{
			TransactionStore.AddOrUpdate(tx);
		}
		if (coinJoin)
		{
			KeyManager.Attributes.CoinJoinTransactions.Remove(txId);
			KeyManager.UpdateFromCoins(Coins);
		}

		BlockchainAnalyzer.Analyze(result.Transaction);

		return result;
	}

	private SmartTransaction[] GetConflictCandidates()
	{
		var allCoins = Coins.AsAllCoinsView().ToArray();
		return TransactionStore.MempoolStore.GetTransactions()
			.Concat(allCoins.Select(coin => coin.Transaction))
			.Concat(allCoins.Where(coin => coin.SpenderTransaction is not null).Select(coin => coin.SpenderTransaction!))
			.Concat(TransactionStore.InvalidatedTransactions.GetTransactions())
			.Where(transaction => !transaction.Confirmed && !TransactionStore.ConfirmedStore.Contains(transaction.GetHash()))
			.DistinctBy(transaction => transaction.GetHash())
			.ToArray();
	}

	private ConfirmedConflictGraph BuildConfirmedConflictGraph()
	{
		var candidates = GetConflictCandidates().ToDictionary(transaction => transaction.GetHash());
		var ancestors = new HashSet<uint256>();
		void Visit(SmartTransaction transaction)
		{
			if (!ancestors.Add(transaction.GetHash())) { return; }
			foreach (var input in transaction.Transaction.Inputs)
			{
				if (candidates.TryGetValue(input.PrevOut.Hash, out var parent)) { Visit(parent); }
			}
		}
		foreach (var coin in Coins.AsAllCoinsView())
		{
			if (!coin.Transaction.Confirmed) { Visit(coin.Transaction); }
			if (coin.SpenderTransaction is { Confirmed: false } spender) { Visit(spender); }
		}
		var rootsByInput = candidates.Values.Where(candidate => ancestors.Contains(candidate.GetHash()))
			.SelectMany(transaction => transaction.Transaction.Inputs.Select(input => (input.PrevOut, Transaction: transaction)))
			.GroupBy(item => item.PrevOut).ToDictionary(group => group.Key, group => group.Select(item => item.Transaction).Distinct().ToArray());
		var childrenByParent = candidates.Values
			.SelectMany(transaction => transaction.Transaction.Inputs.Select(input => input.PrevOut.Hash).Distinct().Select(parent => (Parent: parent, Transaction: transaction)))
			.GroupBy(item => item.Parent).ToDictionary(group => group.Key, group => group.Select(item => item.Transaction).ToArray());
		return new ConfirmedConflictGraph(rootsByInput, childrenByParent);
	}

	private sealed record ConfirmedConflictGraph(Dictionary<OutPoint, SmartTransaction[]> RootsByInput, Dictionary<uint256, SmartTransaction[]> ChildrenByParent);

	private void InvalidateConfirmedConflicts(SmartTransaction confirmed, HashSet<SmartTransaction> conflicts, ProcessedResult result, ConfirmedConflictGraph graph)
	{
		// Include local registry objects: another wallet sharing the store may already
		// have removed the same transactions before this processor sees the block.
		var invalidated = new List<SmartTransaction>();
		var visited = new HashSet<uint256>();
		void Visit(SmartTransaction transaction)
		{
			var id = transaction.GetHash();
			if (transaction.Confirmed || !visited.Add(id)) { return; }
			if (graph.ChildrenByParent.TryGetValue(id, out var children))
			{
				foreach (var child in children) { Visit(child); }
			}
			invalidated.Add(transaction);
		}
		foreach (var conflict in conflicts.ToArray()) { Visit(conflict); }
		if (invalidated.Count == 0) { return; }

		ConfirmedConflictDetected?.Invoke(confirmed, invalidated);
		foreach (var transaction in invalidated)
		{
			// Keep independent raw copies: active SmartTransaction objects can later
			// confirm or be reused by another wallet or by the broadcast store.
			var archived = new SmartTransaction(transaction.Transaction.Clone(), Height.Mempool, labels: transaction.Labels,
				isReplacement: transaction.IsReplacement, isSpeedup: transaction.IsSpeedup,
				isCancellation: transaction.IsCancellation, firstSeen: transaction.FirstSeen);
			TransactionStore.InvalidatedTransactions.TryAddOrUpdate(archived);
		}
		foreach (var transaction in invalidated)
		{
			// Undo children first: their independent wallet inputs must become available
			// again before removing outputs of the invalidated parent transaction.
			var (removed, restored) = Coins.Undo(transaction.GetHash());
			foreach (var coin in removed)
			{
				coin.Transaction.TryRemoveWalletOutput(coin);
			}
			foreach (var coin in restored)
			{
				transaction.TryRemoveWalletInput(coin);
			}
			if (transaction.IsReplacement) { result.ReplacedCoins.AddRange(removed); }
			else { result.SuccessfullyDoubleSpentCoins.AddRange(removed); }
			result.RestoredCoins.AddRange(restored);
			Aware.Remove(transaction.GetHash());
			conflicts.Add(transaction);
		}
		var removedOutpoints = result.ReplacedCoins.Concat(result.SuccessfullyDoubleSpentCoins).Select(coin => coin.Outpoint).ToHashSet();
		result.RestoredCoins.RemoveAll(coin => removedOutpoints.Contains(coin.Outpoint));
	}

	private bool CanBeConsideredDustAttack(TxOut output, HdPubKey hdPubKey, bool weAreAmongTheSender) =>
		output.Value <= DustThreshold // the value received is under the dust threshold
		&& !weAreAmongTheSender // we are not one of the senders (it is not a self-spending tx or coinjoin)
		&& Coins.IsUsed(hdPubKey); // the destination address has already been used (address reuse)

	private void SaveInternalKeysLatestSpendingHeight(Height txHeight, IEnumerable<HdPubKey> internalKeys)
	{
		foreach (var spenderKey in internalKeys)
		{
			if (Coins.HasUnspentCoin(spenderKey))
			{
				// The key still has unspent coins.
				continue;
			}

			// All the coins on this key were spent. Mark it as retired and store the block height.
			if (spenderKey.LatestSpendingHeight is null)
			{
				spenderKey.LatestSpendingHeight = txHeight;
			}
			else if ((Height)spenderKey.LatestSpendingHeight < txHeight)
			{
				// Key spent its coins earlier in history but was reused and spent again.
				spenderKey.LatestSpendingHeight = txHeight;
			}
		}
	}

	public void UndoBlock(Height blockHeight)
	{
		Coins.SwitchToUnconfirmFromBlock(blockHeight);
	}
}
