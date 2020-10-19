using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Bases;
using WalletWasabi.Helpers;
using WalletWasabi.Models;
using WalletWasabi.WebClients.Wasabi;

namespace WalletWasabi.Services
{
	public class UpdateChecker : PeriodicRunner
	{
		private UpdateStatus _updateStatus;

		public UpdateChecker(TimeSpan period, WasabiSynchronizer synchronizer) : base(period)
		{
			Synchronizer = Guard.NotNull(nameof(synchronizer), synchronizer);
			UpdateStatus = new UpdateStatus(true, true, new Version(), 0);

			Synchronizer.PropertyChanged += Synchronizer_PropertyChanged;
		}

		public event EventHandler<UpdateStatus>? UpdateStatusChanged;

		private WasabiSynchronizer Synchronizer { get; }

		public UpdateStatus UpdateStatus
		{
			get => _updateStatus;
			private set
			{
				if (value != _updateStatus)
				{
					_updateStatus = value;
					UpdateStatusChanged?.Invoke(this, value);
				}
			}
		}

		private void Synchronizer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(WasabiSynchronizer.BackendStatus) &&
				Synchronizer.BackendStatus == BackendStatus.Connected)
			{
				// Any time when the synchronizer detects the backend, we immediately check the versions. GUI relies on UpdateStatus changes.
				TriggerRound();
			}
		}

		protected override async Task ActionAsync(CancellationToken cancel)
		{
			using WasabiClient wasabiClient = Synchronizer.WasabiClientFactory.NewBackendClient();
			UpdateStatus = await wasabiClient.CheckUpdatesAsync(cancel).ConfigureAwait(false);
		}

		public override void Dispose()
		{
			Synchronizer.PropertyChanged -= Synchronizer_PropertyChanged;
			base.Dispose();
		}
	}
}
