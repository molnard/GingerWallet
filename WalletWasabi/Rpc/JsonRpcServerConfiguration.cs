namespace WalletWasabi.Rpc;

public class JsonRpcServerConfiguration
{
	public JsonRpcServerConfiguration(bool enabled, string jsonRpcUser, string jsonRpcPassword, string[] prefixes)
	{
		IsEnabled = enabled;
		JsonRpcUser = jsonRpcUser;
		JsonRpcPassword = jsonRpcPassword;
		Prefixes = prefixes;

		if (IsEnabled && !RequiresCredentials)
		{
			throw new ArgumentException("The JSON-RPC server requires both a non-empty user name and password.");
		}
	}

	public bool IsEnabled { get; }
	public string JsonRpcUser { get; }
	public string JsonRpcPassword { get; }
	public string[] Prefixes { get; }

	public bool RequiresCredentials => !string.IsNullOrWhiteSpace(JsonRpcUser) && !string.IsNullOrWhiteSpace(JsonRpcPassword);
}
