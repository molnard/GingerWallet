using System;

namespace WalletWasabi.TrezorSuite;

public sealed record TrezorSuiteIntegrationStatus(
	string? ExecutablePath,
	string? SuiteVersion,
	bool IsInstalled,
	bool IsBackendAvailable,
	bool IsConfigured,
	bool HasBackup,
	DateTimeOffset? LastVerifiedAt,
	string Message);

public sealed record TrezorSuiteIntegrationState(
	string SuiteExecutablePath,
	bool OriginalSettingsExisted,
	string? OriginalSettingsJson,
	DateTimeOffset? LastVerifiedAt);

internal sealed record TrezorSuiteStoredSettings(bool Exists, string? Json);

public sealed class TrezorSuiteIntegrationException : Exception
{
	public TrezorSuiteIntegrationException(string message)
		: base(message)
	{
	}

	public TrezorSuiteIntegrationException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
