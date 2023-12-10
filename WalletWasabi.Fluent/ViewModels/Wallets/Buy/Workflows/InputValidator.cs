using ReactiveUI;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Buy.Workflows;

public abstract partial class InputValidator : ReactiveObject
{
	private readonly Func<string?>? _messageProvider;
	[AutoNotify] private string? _message;
	[AutoNotify] private string? _watermark;
	[AutoNotify] private string? _content;

	protected InputValidator(
		WorkflowState workflowState,
		Func<string?>? messageProvider,
		string? watermark,
		string? content)
	{
		_messageProvider = messageProvider;
		_watermark = watermark;
		_content = content;
		WorkflowState = workflowState;
	}

	protected WorkflowState WorkflowState { get; }

	public abstract bool IsValid();

	public abstract string? GetFinalMessage();

	public virtual bool CanDisplayMessage()
	{
		return true;
	}

	public virtual void OnActivation()
	{
		WorkflowState.SignalValid(false);

		if (_messageProvider != null)
		{
			Message = _messageProvider.Invoke();
		}
	}

	public virtual bool OnCompletion()
	{
		return true;
	}
}
