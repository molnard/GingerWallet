using NBitcoin;
using System.Collections.Generic;
using System.Linq;
using WalletWasabi.Blockchain.Transactions;

namespace WalletWasabi.SecretHunt;

public class SecretHuntResults
{
	public bool Enabled { get; set; } = false;
	public List<SecretHuntEventResult> Results { get; set; } = [];
}

public class SecretHuntEventResult
{
	public SecretHuntEventResult()
	{
		Event = SecretHuntEvent.EmptyEvent;
	}

	public SecretHuntEventResult(SecretHuntEvent huntEvent)
	{
		Event = huntEvent;
	}

	public SecretHuntEvent Event { get; set; }

	// if ExtraSecret is not null that means the client already asked for it and finished
	public string? ExtraSecret { get; set; } = null;

	public SortedList<string, List<uint256>> Secrets { get; set; } = [];
	public List<uint256> CheckedWithNoResults { get; set; } = [];

	private static TimeSpan ExtraTime = TimeSpan.FromDays(5);

	public void AddCheckedTransaction(SmartTransaction tx)
	{
		if (IsNewCandidate(tx))
		{
			CheckedWithNoResults.Add(tx.GetHash());
		}
	}

	public void AddMatchingTransaction(SmartTransaction tx, string[] res)
	{
		uint256 hash = tx.GetHash();
		foreach (var secret in res)
		{
			int idx = Secrets.IndexOfKey(secret);
			if (idx == -1)
			{
				Secrets[secret] = [hash];
				return;
			}
			var list = Secrets.Values[idx];
			if (!list.Contains(hash))
			{
				list.Add(hash);
			}
		}
	}

	public bool IsNewCandidate(SmartTransaction tx) => IsNewCandidate(tx.GetHash(), tx.FirstSeen);

	public bool IsNewCandidate(uint256 txId, DateTimeOffset confirmationTime)
	{
		if (ExtraSecret is not null || (Event.Weights.Length <= Secrets.Count))
		{
			return false;
		}

		// The exact confirmation time is unknown, so we make more allowance (5 days) for checking
		if (Event.StartDate - confirmationTime > ExtraTime || confirmationTime - Event.EndDate > TimeSpan.Zero)
		{
			return false;
		}
		if (CheckedWithNoResults.Contains(txId) || Secrets.SelectMany(x => x.Value).Contains(txId))
		{
			return false;
		}
		return true;
	}

	// Simple Equals, no extra Equals(object?) and then GetHashCode(), we don't need them
	public bool Equals(SecretHuntEventResult? other)
	{
		if (this == other)
		{
			return true;
		}
		if (other is null || !Event.Equals(other.Event) || ExtraSecret != other.ExtraSecret || other.Secrets.Count != Secrets.Count || !CheckedWithNoResults.SequenceEqual(other.CheckedWithNoResults))
		{
			return false;
		}
		for (int idx = 0, len = Secrets.Count; idx < len; idx++)
		{
			if (Secrets.GetKeyAtIndex(idx) != other.Secrets.GetKeyAtIndex(idx) || !Secrets.GetValueAtIndex(idx).SequenceEqual(other.Secrets.GetValueAtIndex(idx)))
			{
				return false;
			}
		}
		return true;
	}
}
