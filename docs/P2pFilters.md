# Bitcoin peer compact filters

Set `"UseP2pFilters": true` in the application configuration, or start Ginger with
`--usep2pfilters=true`, then restart. The default remains backend filters.

In this mode Ginger downloads standard BIP158 basic filters from Bitcoin peers
advertising `NODE_COMPACT_FILTERS`. The existing Tor configuration also applies
to these connections. On mainnet and testnet it requires matching filter-hash
responses from two peer network groups. Regtest permits the configured local
peer. Peers with incomplete, inconsistent or malformed responses are disconnected
and replaced; there is no automatic fallback to backend filters.

Ginger checks proof of work through NBitcoin's header synchronization, compares
mainnet block and filter headers with Wasabi v2.8.3 checkpoints, verifies every
filter's block and hash, and decodes its data before storage. A same-height
reorganization rolls back filters and wallet transactions using the existing
reorg path. Full block headers are cached and filter headers are authenticated
again on restart. A cached filter with a mismatching authenticated hash is rolled
back and downloaded again through the same reorg path.

The basic-filter cache is `BitcoinStore/<network>/IndexStore/IndexStore.Bip158.sqlite`.
The existing backend cache is retained separately. Switching modes may rescan
wallet history against the selected cache; wallet keys and transaction storage
are retained. The shared basic-filter cache starts at the conservative mainnet
activation checkpoint (genesis on testnet/regtest), so importing an older wallet
does not require discarding a newer wallet's birthday or trusted filter history.
The first basic-filter download is larger than Ginger's existing witness-only
filter download.

Fresh BIP39 software wallets persist a birthday at the most recent checkpoint at least
one day old. Filters before that birthday do not cause script matching or block
downloads. Existing wallets without a birthday, recovered wallets and imported
watch-only/hardware wallets continue scanning their full supported history.
An explicit wallet rescan clears the birthday and honors the chosen starting
height. Birthdays apply to both backend and peer filter modes.

Backend services for fees, rates, updates, mempool cleanup and CoinJoin continue
using their existing configuration. Peer filter synchronization and wallet
loading can proceed while the backend is unavailable.
