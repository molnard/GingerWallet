using NBitcoin;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Io;
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
	private Journal _journal;

	public WalletTransactionJournal(KeyManager keyManager)
	{
		_keyManager = keyManager;
		_network = keyManager.GetNetwork();
		var file = FileManager;
		_journal = file?.Exists() is true
			? JsonSerializer.Deserialize<Journal>(file.ReadAllText(Encoding.UTF8)) ?? throw new InvalidOperationException("Invalid wallet transaction journal.")
			: new Journal();
		if (file?.Exists() is true && _journal.Network is null)
		{
			throw new InvalidOperationException("Wallet transaction journal has no network.");
		}
		if (_journal.Network is { } network && network != _network.ToString())
		{
			if (Network.GetNetwork(network) is null)
			{
				throw new InvalidOperationException("Wallet transaction journal has an invalid network.");
			}
			// KeyManager clears its block state when the selected network changes; proofs from the previous network cannot apply either.
			_journal = new Journal();
		}
		_journal.Network = _network.ToString();
		if (_journal.Pending is null || _journal.Conflicts is null
			|| _journal.Pending.Any(x => x.NextHeight >= Height.Mempool.Value)
			|| _journal.Pending.Select(x => x.Transaction.Hash).Distinct().Count() != _journal.Pending.Count
			|| _journal.Conflicts.Any(x => x.Height >= Height.Mempool.Value || !uint256.TryParse(x.BlockHash, out _) || x.Transactions is null))
		{
			throw new InvalidOperationException("Invalid wallet transaction journal state.");
		}
		foreach (var transaction in _journal.Pending.Select(x => x.Transaction).Concat(_journal.Conflicts.SelectMany(x => x.Transactions)))
		{
			_ = transaction.ToTransaction(_network);
		}
	}

	public static string GetFilePath(string walletFilePath) => walletFilePath + ".transactions";
	private SafeIoManager? FileManager => _keyManager.FilePath is { } path ? new SafeIoManager(GetFilePath(path)) : null;

	/// <summary>Copy first, retaining the original until the wallet file operation has succeeded.</summary>
	public static bool Copy(string sourceWalletPath, string destinationWalletPath, bool overwrite = false)
	{
		var destination = new SafeIoManager(GetFilePath(destinationWalletPath));
		if (!overwrite && (destination.Exists() || File.Exists(destination.NewFilePath)))
		{
			throw new IOException("A wallet transaction journal already exists at the destination.");
		}
		var source = new SafeIoManager(GetFilePath(sourceWalletPath));
		if (!source.Exists())
		{
			return false;
		}
		destination.WriteAllText(source.ReadAllText(Encoding.UTF8), Encoding.UTF8);
		return true;
	}

	public static void Delete(string walletFilePath) => new SafeIoManager(GetFilePath(walletFilePath)).DeleteMe();
	public static bool Exists(string walletFilePath) => new SafeIoManager(GetFilePath(walletFilePath)).Exists();

	public uint? NextScanHeight => _journal.Pending.Count == 0 ? null : _journal.Pending.Min(x => x.NextHeight);
	public IReadOnlyList<Conflict> Conflicts => _journal.Conflicts;
	public bool IsInvalidated(uint256 hash) => _journal.Conflicts.Any(x => x.Transactions.Any(t => t.Hash == hash.ToString()));
	public IEnumerable<SmartTransaction> PendingTransactions => _journal.Pending.Select(x => x.Transaction.ToTransaction(_network));

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

	public uint256[] GetPendingThrough(uint height) => _journal.Pending.Where(x => x.NextHeight <= height).Select(x => uint256.Parse(x.Transaction.Hash)).ToArray();

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
		var next = JsonSerializer.Deserialize<Journal>(JsonSerializer.Serialize(_journal))!;
		update(next);
		// Persist before publishing the changed state. A failed write must leave every operation safely retryable.
		FileManager?.WriteAllText(JsonSerializer.Serialize(next), Encoding.UTF8);
		_journal = next;
	}

	internal sealed class Journal
	{
		public Journal() { }

		public string? Network { get; set; }
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

	internal sealed class TransactionData
	{
		public TransactionData() { }

		public string Hash { get; set; } = "";
		public string Hex { get; set; } = "";
		public string Labels { get; set; } = "";
		public long FirstSeen { get; set; }
		public bool Replacement { get; set; }
		public bool Speedup { get; set; }
		public bool Cancellation { get; set; }

		public static TransactionData FromTransaction(SmartTransaction transaction) => new()
		{
			Hash = transaction.GetHash().ToString(),
			Hex = transaction.Transaction.ToHex(),
			Labels = transaction.Labels.ToString(),
			FirstSeen = transaction.FirstSeen.ToUnixTimeSeconds(),
			Replacement = transaction.IsReplacement,
			Speedup = transaction.IsSpeedup,
			Cancellation = transaction.IsCancellation
		};

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
