namespace WalletWasabi.Fluent.ViewModels.Wallets.Buy.Workflows.ShopinBit;

public partial class DefaultWorkflowInputValidatorViewModel : WorkflowInputValidatorViewModel
{
	public DefaultWorkflowInputValidatorViewModel(string? message) : base(message)
	{
	}

	public override bool IsValid()
	{
		// TODO: Validate request.
		return !string.IsNullOrWhiteSpace(Message);
	}

	public override string? GetFinalMessage()
	{
		if (IsValid())
		{
			return Message;
		}

		return null;
	}
}
