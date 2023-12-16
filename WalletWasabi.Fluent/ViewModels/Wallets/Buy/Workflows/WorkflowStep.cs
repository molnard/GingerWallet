using ReactiveUI;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using WalletWasabi.BuyAnything;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Buy.Workflows;

public interface IWorkflowStep
{
	//Conversation Conversation { get; set; }

	IAsyncEnumerable<Conversation> ExecuteAsync(Conversation conversation);

	Task<(Conversation Conversation, ChatMessage NewMessage)> EditMessageAsync(Conversation conversation, ChatMessage chatMessage);

	void Ignore();

	ICommand SendCommand { get; }
}

/// <summary>
/// Unifies the WorkflowStep class and the InputValidator class in a simple design
/// Supports adding bot messages to the conversation and then waiting for user input
/// </summary>
/// <typeparam name="TValue">The type of the Value input by the user</typeparam>
/// <remarks>this Class works in a stateless fashion. ExecuteAsync() receives a Conversation and returns the modified version of that same conversation, including bot messages, user messages, and metadata.</remarks>
public abstract partial class WorkflowStep<TValue> : ReactiveObject, IWorkflowStep
{
	private readonly TaskCompletionSource _userInputTcs = new();

	//[AutoNotify] private Conversation _conversation;
	[AutoNotify] private string _caption = "Send";

	[AutoNotify] private TValue? _value;
	[AutoNotify] private bool _isValid;
	private bool _ignored;

	public WorkflowStep(Conversation conversation)
	{
		//_conversation = conversation;

		// Retrieve initial value from the conversation. If the conversation was already stored it might contain a value for this Step, and thus this Step will not wait for user input.
		_value = RetrieveValue(conversation);

		// if this step already contains data previously stored in the Conversation (retrieved by RetrieveData),
		// then set the Step as completed so the parent workflow can move on.
		if (ValidateInitialValue(_value))
		{
			SetCompleted();
		}

		// When Value changes, call IsValidValue(Value) and set IsValid property accordingly.
		this.WhenAnyValue(x => x.Value)
			.Select(ValidateUserValue)
			.BindTo(this, x => x.IsValid);

		SendCommand = ReactiveCommand.Create(Send, this.WhenAnyValue(x => x.IsValid));
	}

	public ICommand SendCommand { get; }

	private string StepName => GetType().Name;

	/// <summary>
	/// Executes the Step, adding any relevant Bot Messages, waiting for user input, and updating Conversation Metadata accordingly
	/// </summary>
	/// <returns>The updated Conversation with newly added Bot messages (if any), User messages, and Metadata</returns>
	public virtual async IAsyncEnumerable<Conversation> ExecuteAsync(Conversation conversation)
	{
		// Only ask the question if it hasn't been asked before
		if (!conversation.ChatMessages.Any(x => x.StepName == StepName && x.Source == MessageSource.Bot))
		{
			var botMessages = BotMessages(conversation).ToArray();

			foreach (var message in botMessages)
			{
				conversation = conversation.AddBotMessage(message, null, StepName);
			}

			yield return conversation;
		}

		// Wait for user confirmation (Send button)
		await _userInputTcs.Task;

		if (_ignored)
		{
			yield return conversation;
		}

		if (Value is { } value)
		{
			// Update the Conversation Metadata with the current user-input value
			conversation = PutValue(conversation, value);

			if (StringValue(value) is { } userMessage)
			{
				// Update the Conversation and add a User Message with the current user-input value represented as text (if any)
				conversation = conversation.AddUserMessage(userMessage, StepName);
			}
		}

		yield return conversation;
	}

	public virtual async Task<(Conversation Conversation, ChatMessage NewMessage)> EditMessageAsync(Conversation conversation, ChatMessage chatMessage)
	{
		// Wait for user confirmation (Send button)
		await _userInputTcs.Task;

		if (_ignored)
		{
			return (conversation, chatMessage);
		}

		// TODO:
		ChatMessage newMessage = null;

		if (Value is { } value)
		{
			// Update the Conversation Metadata with the current user-input value
			conversation = PutValue(conversation, value);

			if (StringValue(value) is { } userMessage)
			{
				// TODO: remove the existing chatMessage from conversation.Chat and insert the new one
				// this will create an entire new Conversation object, because it's fully immutable
				// conversation = conversation.ReplaceMessage(chatMessage, newChatMessage);
			}
		}

		return (conversation, newMessage);
	}

	/// <summary>
	/// the question[s] the bot asks to the user before waiting for their input.
	/// </summary>
	protected virtual IEnumerable<string> BotMessages(Conversation conversation)
	{
		yield break;
	}

	/// <summary>
	/// Signal Step completion.
	/// </summary>
	protected void SetCompleted() => _userInputTcs.SetResult();

	/// <summary>
	/// Ignores the Step. Used when another step is executed due to external reasons (such as Offer Received)
	/// </summary>
	public void Ignore()
	{
		_ignored = true;
		SetCompleted();
	}

	/// <summary>
	/// Validate the Step's Value entered by the User. Fires when Value changes and is used to set IsValid property and the CanExecute of the SendCommand
	/// </summary>
	protected virtual bool ValidateUserValue(TValue? value) => value is { };

	/// <summary>
	/// Validate the Step's initial value retrieved from an existing Conversation.
	/// </summary>
	/// <param name="value"></param>
	/// <returns></returns>
	protected virtual bool ValidateInitialValue(TValue? value) => value is { };

	/// <summary>
	/// Retrieve the Step's Value from the Conversation Metadata
	/// </summary>
	protected abstract TValue? RetrieveValue(Conversation conversation);

	/// <summary>
	/// Add the Step's value into the Conversation Metadata
	/// </summary>
	protected abstract Conversation PutValue(Conversation conversation, TValue value);

	/// <summary>
	/// String representation of the Step's value (to show it as a User message)
	/// </summary>
	protected virtual string? StringValue(TValue value) => null;

	/// <summary>
	/// This is called when the user presses the "Send" button. It simply signals the TaskCompletionSource so that ExecuteAsync() can move on.
	/// </summary>
	private void Send()
	{
		if (IsValid)
		{
			SetCompleted();
		}
	}
}
