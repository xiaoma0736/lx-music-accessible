# ghacc: Alibaba Cloud Linux GitHub accelerator

`ghacc` is an on-demand Bash wrapper. It never changes global or repository Git configuration.

## Requirements

- Bash to run `ghacc`.
- Git for `clone`, `pull`, `fetch`, and `git` commands, including the credential helper or SSH setup those Git commands use.
- `curl` (preferred) or `wget` for mirror checks, downloads, and GitHub release metadata. The download and release commands fail if neither tool is installed.

## Install

```bash
sudo install -m 0755 linux/ghacc.sh /usr/local/bin/ghacc
sudo install -m 0644 linux/ghacc.conf.example /etc/ghacc.conf
```

Edit `/etc/ghacc.conf` to put your preferred mirror first. The sourced `GHACC_MIRRORS` array controls acceleration order.

## Common commands

```bash
ghacc status
ghacc clone https://github.com/owner/repo.git
ghacc pull
ghacc fetch
ghacc git pull origin main
ghacc archive owner/repo main
ghacc release owner/repo latest
ghacc release owner/repo v1.2.3 app-linux-amd64.tar.gz
ghacc download https://github.com/owner/repo/releases/download/v1.2.3/example.zip
ghacc download https://raw.githubusercontent.com/owner/repo/main/README.md
```

With `GHACC_AUTO_SELECT=1`, each mirror is probed before use. After a mirror passes its probe but the actual Git, download, archive, or GitHub API operation fails, `ghacc` tries the next configured mirror before considering direct GitHub. With `GHACC_AUTO_SELECT=0`, configured mirrors are attempted in order without probes. Direct GitHub is used only when all mirror operations fail and `GHACC_FALLBACK_DIRECT=1`; setting it to `0` disables that fallback. Interrupted operations preserve status 130.

## Configuration precedence

Process environment values for `GHACC_TIMEOUT`, `GHACC_DOWNLOAD_DIR`, `GHACC_AUTO_SELECT`, and `GHACC_FALLBACK_DIRECT` override the values in `/etc/ghacc.conf` after the file is sourced. `GHACC_MIRRORS` is controlled by that file. Empty environment values are intentional: for example, `GHACC_DOWNLOAD_DIR=""` saves downloads in the current working directory. `GHACC_TIMEOUT` must be a nonnegative integer number of seconds.

## Private repositories

Git operations delegate authentication to your existing Git credential helper or SSH setup. That authentication does not authenticate GitHub API release listing: for a private repository, `ghacc release` can still fail unless your curl/wget environment independently supplies valid GitHub API credentials.
