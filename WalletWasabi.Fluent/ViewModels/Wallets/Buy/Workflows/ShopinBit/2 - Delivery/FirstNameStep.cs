﻿using System.Collections.Generic;
using WalletWasabi.BuyAnything;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Buy.Workflows;

public class FirstNameStep : TextInputStep
{
	public FirstNameStep(Conversation conversation) : base(conversation)
	{
	}

	protected override IEnumerable<string> BotMessages(Conversation conversation)
	{
		yield return "To proceed, we'll need some details for delivery and billing.";

		yield return "First Name:";
	}

	protected override Conversation PutValue(Conversation conversation, string value) =>
		conversation.UpdateMetadata(m => m with { FirstName = value });

	protected override string? RetrieveValue(Conversation conversation) =>
		conversation.MetaData.FirstName;
}
