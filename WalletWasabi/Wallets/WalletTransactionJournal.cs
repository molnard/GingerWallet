using NBitcoin;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;

namespace WalletWasabi.Wallets;

/// <summary>
/// Keeps the information that cannot be reconstructed from the active transaction store after a confirmed conflict.
/// All access is serialized by <see cref="Blockchain.TransactionProcessing.TransactionProcessor.Lock"/>.
/// </summary>
internal sealed class WalletTransactionJournal
{
	private readonly KeyManager _keyManager;
	private readonly Network _network;
	private Journal State => _keyManager.TransactionJournal;

	public WalletTransactionJournal(KeyManager keyManager)
	{
		_keyManager = keyManager;
		_network = keyManager.GetNetwork();
		if (State is null || State.Pending is null || State.Conflicts is null
			|| State.Pending.Any(x => x.NextHeight >= Height.Mempool.Value)
			|| State.Pending.Select(x => x.Transaction.Hash).Distinct().Count() != State.Pending.Count
			|| State.Conflicts.Any(x => x.Height >= Height.Mempool.Value || !uint256.TryParse(x.BlockHash, out _) || x.Transactions is null))
		{
			throw new InvalidOperationException("Invalid wallet transaction journal state.");
		}
		foreach (var transaction in State.Pending.Select(x => x.Transaction).Concat(State.Conflicts.SelectMany(x => x.Transactions)))
		{
			_ = transaction.ToTransaction(_network);
		}
	}

	public uint? NextScanHeight => State.Pending.Count == 0 ? null : State.Pending.Min(x => x.NextHeight);
	public IReadOnlyList<Conflict> Conflicts => State.Conflicts;
	public bool IsInvalidated(uint256 hash) => State.Conflicts.Any(x => x.Transactions.Any(t => t.Hash == hash.ToString()));
	public IEnumerable<SmartTransaction> PendingTransactions => State.Pending.Select(x => x.Transaction.ToTransaction(_network));

	public void RecordPending(SmartTransaction transaction, uint inclusiveHeight)
	{
		var hash = transaction.GetHash().ToString();
		if (IsInvalidated(transaction.GetHash()))
		{
			return;
		}
		Commit(journal =>
		{
			if (journal.Pending.All(x => x.Transaction.Hash != hash))
			{
				journal.Pending.Add(new Pending { Transaction = TransactionData.FromTransaction(transaction), NextHeight = inclusiveHeight });
			}
		});
	}

	public uint256[] GetPendingThrough(uint height) => State.Pending.Where(x => x.NextHeight <= height).Select(x => uint256.Parse(x.Transaction.Hash)).ToArray();

	public void RewindPending(SmartTransaction transaction, uint inclusiveHeight)
	{
		if (IsInvalidated(transaction.GetHash()))
		{
			return;
		}
		Commit(journal =>
		{
			var pending = journal.Pending.FirstOrDefault(x => x.Transaction.Hash == transaction.GetHash().ToString());
			if (pending is null)
			{
				journal.Pending.Add(new Pending { Transaction = TransactionData.FromTransaction(transaction), NextHeight = inclusiveHeight });
			}
			else
			{
				pending.NextHeight = Math.Min(pending.NextHeight, inclusiveHeight);
			}
		});
	}

	public void CompleteScan(uint height, IReadOnlyCollection<uint256> scanned, IEnumerable<SmartTransaction> pendingTransactions)
	{
		var pending = pendingTransactions.ToDictionary(x => x.GetHash());
		Commit(journal =>
		{
			journal.Pending.RemoveAll(x => !pending.ContainsKey(uint256.Parse(x.Transaction.Hash)));
			foreach (var item in journal.Pending.Where(x => scanned.Contains(uint256.Parse(x.Transaction.Hash))))
			{
				item.NextHeight = Math.Max(item.NextHeight, checked(height + 1));
			}
		});
	}

	public void RecordConflict(SmartTransaction confirmed, IReadOnlyList<SmartTransaction> invalidated)
	{
		if (confirmed.BlockHash is not { } blockHash || !confirmed.Confirmed)
		{
			throw new ArgumentException("A confirmed block is required to invalidate pending transactions.", nameof(confirmed));
		}
		Commit(journal =>
		{
			var conflict = journal.Conflicts.FirstOrDefault(x => x.BlockHash == blockHash.ToString());
			if (conflict is null)
			{
				conflict = new Conflict { BlockHash = blockHash.ToString(), Height = (uint)confirmed.Height.Value };
				journal.Conflicts.Add(conflict);
			}
			foreach (var transaction in invalidated)
			{
				if (conflict.Transactions.All(x => x.Hash != transaction.GetHash().ToString()))
				{
					conflict.Transactions.Add(TransactionData.FromTransaction(transaction));
				}
			}
			journal.Pending.RemoveAll(x => conflict.Transactions.Any(t => t.Hash == x.Transaction.Hash));
		});
	}

	public IReadOnlyList<SmartTransaction> GetInvalidatedTransactions(Conflict conflict) => conflict.Transactions.Select(x => x.ToTransaction(_network)).ToArray();

	public void ReleaseConflict(Conflict conflict, uint inclusiveHeight)
	{
		Commit(journal =>
		{
			journal.Conflicts.RemoveAll(x => x.BlockHash == conflict.BlockHash);
			foreach (var transaction in conflict.Transactions)
			{
				if (journal.Pending.All(x => x.Transaction.Hash != transaction.Hash))
				{
					journal.Pending.Add(new Pending { Transaction = transaction, NextHeight = inclusiveHeight });
				}
			}
		});
	}

	private void Commit(Action<Journal> update)
	{
		var next = JsonSerializer.Deserialize<Journal>(JsonSerializer.Serialize(State))!;
		update(next);
		_keyManager.SetTransactionJournal(next);
	}

	internal sealed class Journal
	{
		public Journal() { }

		public List<Pending> Pending { get; set; } = [];
		public List<Conflict> Conflicts { get; set; } = [];
	}

	internal sealed class Pending
	{
		public Pending() { }

		public TransactionData Transaction { get; set; } = new();
		public uint NextHeight { get; set; }
	}

	internal sealed class Conflict
	{
		public Conflict() { }

		public string BlockHash { get; set; } = "";
		public uint Height { get; set; }
		public List<TransactionData> Transactions { get; set; } = [];
	}

	internal sealed record TransactionData(string Hash = "", string Hex = "", string Labels = "", long FirstSeen = 0,
		bool Replacement = false, bool Speedup = false, bool Cancellation = false)
	{
		public static TransactionData FromTransaction(SmartTransaction transaction) => new(
			transaction.GetHash().ToString(), transaction.Transaction.ToHex(), transaction.Labels.ToString(),
			transaction.FirstSeen.ToUnixTimeSeconds(), transaction.IsReplacement, transaction.IsSpeedup, transaction.IsCancellation);

		public SmartTransaction ToTransaction(Network network)
		{
			var transaction = Transaction.Parse(Hex, network);
			if (transaction.GetHash().ToString() != Hash)
			{
				throw new InvalidOperationException("Wallet journal transaction hash does not match its contents.");
			}
			return new SmartTransaction(transaction, Height.Mempool, labels: new LabelsArray(Labels), isReplacement: Replacement,
				isSpeedup: Speedup, isCancellation: Cancellation, firstSeen: DateTimeOffset.FromUnixTimeSeconds(FirstSeen));
		}
	}
}
