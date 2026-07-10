# GitHub Actions release

Ginger can use a Wasabi-style GitHub Actions release pipeline: a tag that starts with `v` builds the platform packages, uploads them as workflow artifacts, signs the collected assets, and creates a draft GitHub release.

## Workflow

1. Update `WalletWasabi/Helpers/Constants.cs` and `Contrib/ReleaseHighlights.md`.
2. Commit the release changes.
3. Tag the release commit with the full client version, for example `v2.0.25.0`.
4. Push the tag to GitHub.
5. Wait for `.github/workflows/release.yml` to create a draft release.
6. Download and test the release assets before publishing the draft.

The workflow can also be started manually from GitHub Actions by passing the release tag in the `version` input. Manual runs are unsigned by default. Set `code_sign=true` to sign Windows/macOS packages, `release_sign=true` to create PGP and Ginger signatures, and `create_release=true` to publish a draft GitHub release.

## Required GitHub secrets

Windows signing uses Azure Key Vault and AzureSignTool:

- `AZURE_KEY_VAULT_URL`: Azure Key Vault URL, for example `https://<vault>.vault.azure.net/`.
- `AZURE_CERTIFICATE_NAME`: certificate name in the Key Vault.
- `AZURE_TENANT_ID`: Azure AD tenant ID.
- `AZURE_CLIENT_ID`: Azure AD app registration/client ID with permission to sign with the certificate.
- `AZURE_CLIENT_SECRET`: client secret for that app registration.

macOS signing and notarization use Apple Developer ID credentials:

- `MAC_CER`: base64-encoded Developer ID Application `.cer` file.
- `MAC_P12`: base64-encoded `.p12` export containing the Developer ID private key.
- `MAC_P12_PASSWORD`: password for the `.p12` export.
- `MAC_TEAMID`: Apple Developer Team ID.
- `MAC_APPLEID`: Apple ID email used for notarization.
- `MAC_APPLEPSSWD`: app-specific Apple password for notarization.

Release asset signing uses GPG and the Ginger update signature key:

- `SIGNING_PGP_KEY`: armored private GPG key used for detached asset signatures and `SHA256SUMS.asc`.
- `SIGNING_PGP_PASSPHRASE`: passphrase for `SIGNING_PGP_KEY`.
- `SIGNING_GINGER_KEY`: WIF private key whose public key matches `Constants.WasabiPubKey`; it creates `SHA256SUMS.gingersig`.

The GitHub release itself uses the built-in `GITHUB_TOKEN`; no separate GitHub token is required for the draft release.

Unsigned test runs do not need any of these secrets. They leave Windows/macOS packages unsigned and create only a plain `SHA256SUMS` file.

## Notes

Wasabi also has a separate release-published workflow that updates its documentation and website repositories by using repository-dispatch tokens. Ginger should only add that after the target documentation or website repositories and token owners are decided.
