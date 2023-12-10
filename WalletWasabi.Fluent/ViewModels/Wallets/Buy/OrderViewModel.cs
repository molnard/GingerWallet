using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using DynamicData;
using DynamicData.Aggregation;
using ReactiveUI;
using WalletWasabi.BuyAnything;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Fluent.ViewModels.Wallets.Buy.Messages;
using WalletWasabi.Fluent.ViewModels.Wallets.Buy.Workflows.ShopinBit;
using WalletWasabi.Logging;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Buy;

public partial class OrderViewModel : ReactiveObject
{
	private readonly ReadOnlyObservableCollection<MessageViewModel> _messages;
	private readonly SourceList<MessageViewModel> _messagesList;
	private readonly UiContext _uiContext;
	private readonly string _conversationStatus;
	private readonly IOrderManager _orderManager;
	private readonly CancellationToken _cancellationToken;
	private ConversationMetaData _metaData;
	private BuyAnythingManager _buyAnythingManager;
	private WebClients.ShopWare.Models.State[] _statesSource = Array.Empty<WebClients.ShopWare.Models.State>();

	[AutoNotify] private bool _isBusy;
	[AutoNotify] private bool _isCompleted;
	[AutoNotify] private bool _hasUnreadMessages;
	[AutoNotify] private MessageViewModel? _selectedMessage;

	public OrderViewModel(UiContext uiContext,
		int id,
		ConversationMetaData metaData,
		string conversationStatus,
		ShopinBitWorkflowManager workflowManager,
		IOrderManager orderManager,
		CancellationToken cancellationToken)
	{
		Id = id;
		Title = metaData.Title;

		_uiContext = uiContext;
		_metaData = metaData;
		_conversationStatus = conversationStatus;
		WorkflowManager = workflowManager;
		_orderManager = orderManager;
		_cancellationToken = cancellationToken;
		_buyAnythingManager = Services.HostedServices.Get<BuyAnythingManager>();

		WorkflowManager.WorkflowState.NextStepObservable.Skip(1).Subscribe(async _ =>
		{
			await SendAsync(_cancellationToken);
		});

		_messagesList = new SourceList<MessageViewModel>();

		_messagesList
			.Connect()
			.Bind(out _messages)
			.Subscribe();

		HasUnreadMessagesObs = _messagesList.Connect().AutoRefresh(x => x.IsUnread).Filter(x => x.IsUnread is true).Count().Select(i => i > 0);

		SendCommand = ReactiveCommand.CreateFromTask(SendAsync, WorkflowManager.WorkflowState.IsValidObservable);

		CanRemoveObs = this.WhenAnyValue(x => x.WorkflowManager.Id).Select(id => id != ConversationId.Empty);

		RemoveOrderCommand = ReactiveCommand.CreateFromTask(RemoveOrderAsync, CanRemoveObs);

		var hasUserMessages =
			_messagesList.CountChanged.Select(_ => _messagesList.Items.Any(x => x is UserMessageViewModel));

		CanResetObs = WorkflowManager.IdChangedObservable
			.Select(x => BackendId == ConversationId.Empty)
			.CombineLatest(hasUserMessages, (a, b) => a && b);

		ResetOrderCommand = ReactiveCommand.CreateFromTask(ResetOrderAsync, CanResetObs);

		// TODO: Remove this once we use newer version of DynamicData
		HasUnreadMessagesObs.BindTo(this, x => x.HasUnreadMessages);

		// IsUnread flags changed so save it to the disk
		this.WhenAnyValue(x => x.HasUnreadMessages)
			.Where(x => x == false)
			.DoAsync(async _ => await UpdateConversationLocallyAsync(GetChatMessages(), cancellationToken))
			.Subscribe();
	}

	public IObservable<bool> HasUnreadMessagesObs { get; }

	public IObservable<bool> CanRemoveObs { get; }

	public IObservable<bool> CanResetObs { get; }

	public ConversationId BackendId => WorkflowManager.Id;

	public string Title { get; }

	public ReadOnlyObservableCollection<MessageViewModel> Messages => _messages;

	public ShopinBitWorkflowManager WorkflowManager { get; }

	public ICommand SendCommand { get; }

	public ICommand RemoveOrderCommand { get; }

	public ICommand ResetOrderCommand { get; }

	public int Id { get; }

	// TODO: Fragile as f*ck! Workflow management needs to be rewritten.
	public async Task StartConversationAsync(string conversationStatus, Country? country)
	{
		if (country != null)
		{
			_statesSource = await _buyAnythingManager.GetStatesForCountryAsync(country.Name, _cancellationToken);
		}

		// The conversation is empty so just start from the beginning
		if (conversationStatus == "Started" && !Messages.Any())
		{
			WorkflowManager.SelectNextShopinBitWorkflow(null, _statesSource);
			WorkflowManager.InvokeOutputWorkflows(AddAssistantMessage, _cancellationToken);
			return;
		}

		if (conversationStatus == "Started")
		{
			WorkflowManager.SelectNextShopinBitWorkflow("Support", _statesSource);
			WorkflowManager.InvokeOutputWorkflows(AddAssistantMessage, _cancellationToken);
			return;
		}

		WorkflowManager.SelectNextShopinBitWorkflow(conversationStatus, _statesSource);
		WorkflowManager.InvokeOutputWorkflows(AddAssistantMessage, _cancellationToken);
	}

	public async Task UpdateOrderAsync(ConversationId id,
		string? conversationStatus,
		string? orderStatus,
		IReadOnlyList<MessageViewModel>? messages,
		ConversationMetaData conversationMetaData,
		CancellationToken cancellationToken)
	{
		if (id != BackendId)
		{
			return;
		}

		_metaData = conversationMetaData;

		if (_metaData.Country is { } c)
		{
			_statesSource = await _buyAnythingManager.GetStatesForCountryAsync(c.Name, cancellationToken);
		}

		IsCompleted = orderStatus == "Done";

		if (messages is not null)
		{
			UpdateMessages(messages);
		}

		if (conversationStatus is not null && _conversationStatus != conversationStatus)
		{
			WorkflowManager.OnInvokeNextWorkflow(conversationStatus, _statesSource, AddAssistantMessage, cancellationToken);
		}
	}

	private async Task SendAsync(CancellationToken cancellationToken)
	{
		IsBusy = true;

		try
		{
			var result = WorkflowManager.InvokeInputWorkflows(AddUserMessage, AddAssistantMessage, _statesSource, cancellationToken);
			if (!result)
			{
				return;
			}

			if (WorkflowManager.CurrentWorkflow.IsCompleted)
			{
				var chatMessages = GetChatMessages();
				await SendApiRequestAsync(chatMessages, _metaData, cancellationToken);
				await SendChatHistoryAsync(GetChatMessages(), cancellationToken);

				WorkflowManager.OnInvokeNextWorkflow(null, _statesSource, AddAssistantMessage, cancellationToken);
			}
		}
		catch (Exception exception)
		{
			await ShowErrorAsync("Error while processing order.");
			Logger.LogError($"Error while processing order: {exception}).");
		}
		finally
		{
			IsBusy = false;
		}
	}

	private void AddAssistantMessage(string message)
	{
		var assistantMessage = new AssistantMessageViewModel(null, null)
		{
			Message = message
		};

		_messagesList.Edit(x =>
		{
			x.Add(assistantMessage);
		});

		SelectedMessage = assistantMessage;
	}

	private void AddUserMessage(string message)
	{
		var editCommand = WorkflowManager.CurrentWorkflow.EditStepCommand;
		var canEditObservable = WorkflowManager.CurrentWorkflow.CanEditObservable;
		var workflowStep = WorkflowManager.CurrentWorkflow.CurrentStep;

		var userMessage = new UserMessageViewModel(editCommand, canEditObservable, workflowStep)
		{
			Message = message
		};

		_messagesList.Edit(x =>
		{
			x.Add(userMessage);
		});

		SelectedMessage = userMessage;
	}

	private async Task RemoveOrderAsync()
	{
		var confirmed = await _uiContext.Navigate().To().ConfirmDeleteOrderDialog(this).GetResultAsync();

		if (confirmed)
		{
			_orderManager.RemoveOrderAsync(Id);
		}
	}

	private async Task ShowErrorAsync(string message)
	{
		await _uiContext.Navigate().To().ShowErrorDialog(message, "Send Failed", "Wasabi was unable to send your message", NavigationTarget.CompactDialogScreen).GetResultAsync();
	}

	private async Task ResetOrderAsync()
	{
		ClearMessages();
		WorkflowManager.ResetWorkflow();
		await StartConversationAsync("Started", null);
	}

	public void UpdateMessages(IReadOnlyList<MessageViewModel> messages)
	{
		// TODO: We need to sync with current workflow.
		_messagesList.Edit(x =>
		{
			x.Clear();
			x.Add(messages);
		});
	}

	private void ClearMessages()
	{
		_messagesList.Edit(x =>
		{
			x.Clear();
		});
	}

	private ChatMessage[] GetChatMessages()
	{
		return _messages
			.Select(x =>
			{
				var message = x.Message ?? "";

				if (x is AssistantMessageViewModel)
				{
					return new ChatMessage(false, message, x.IsUnread); // This method is only called when Workflow == IsCompleted, so I guess every message is read at this point.
				}

				return new ChatMessage(true, message, x.IsUnread);
			})
			.ToArray();
	}

	private Task UpdateConversationLocallyAsync(ChatMessage[] chatMessages, CancellationToken cancellationToken)
	{
		if (WorkflowManager.Id == ConversationId.Empty || WorkflowManager.CurrentWorkflow is null || Services.HostedServices.GetOrDefault<BuyAnythingManager>() is not { } buyAnythingManager)
		{
			return Task.CompletedTask;
		}

		return buyAnythingManager.UpdateConversationOnlyLocallyAsync(WorkflowManager.Id, chatMessages, cancellationToken);
	}

	private Task SendChatHistoryAsync(ChatMessage[] chatMessages, CancellationToken cancellationToken)
	{
		if (WorkflowManager.Id == ConversationId.Empty || WorkflowManager.CurrentWorkflow is null || Services.HostedServices.GetOrDefault<BuyAnythingManager>() is not { } buyAnythingManager)
		{
			return Task.CompletedTask;
		}

		return buyAnythingManager.UpdateConversationAsync(WorkflowManager.Id, chatMessages, cancellationToken);
	}

	private async Task SendApiRequestAsync(ChatMessage[] chatMessages, ConversationMetaData metaData, CancellationToken cancellationToken)
	{
		if (WorkflowManager.CurrentWorkflow is null || Services.HostedServices.GetOrDefault<BuyAnythingManager>() is not { } buyAnythingManager)
		{
			return;
		}

		var request = WorkflowManager.CurrentWorkflow.GetResult();

		switch (request)
		{
			case InitialWorkflowRequest initialWorkflowRequest:
				{
					if (initialWorkflowRequest.Location is not { } location ||
						initialWorkflowRequest.Product is not { } product ||
						initialWorkflowRequest.Request is not { } requestMessage) // TODO: Delete, this is redundant, we send out the whole conversation to generate a new order.
					{
						throw new ArgumentException($"Argument was not provided!");
					}

					metaData = metaData with { Country = location };

					await buyAnythingManager.StartNewConversationAsync(
						WorkflowManager.WalletId,
						location.Id,
						product,
						chatMessages,
						metaData,
						cancellationToken);
					break;
				}
			case DeliveryWorkflowRequest deliveryWorkflowRequest:
				{
					if (deliveryWorkflowRequest.FirstName is not { } firstName ||
						deliveryWorkflowRequest.LastName is not { } lastName ||
						deliveryWorkflowRequest.StreetName is not { } streetName ||
						deliveryWorkflowRequest.HouseNumber is not { } houseNumber ||
						deliveryWorkflowRequest.PostalCode is not { } postalCode ||
						// TODO: deliveryWorkflowRequest.State is not { } state ||
						deliveryWorkflowRequest.City is not { } city ||
						metaData.Country is not { } country
					   )
					{
						throw new ArgumentException($"Argument was not provided!");
					}

					var state = deliveryWorkflowRequest.State;

					await buyAnythingManager.AcceptOfferAsync(
						WorkflowManager.Id,
						firstName,
						lastName,
						streetName,
						houseNumber,
						postalCode,
						city,
						state is not null ? state.Id : "stateId", // TODO: use state variable, but ID is required, not name.
						country.Id,
						cancellationToken);
					break;
				}
		}
	}
}
