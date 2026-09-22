# ghacc: Alibaba Cloud Linux GitHub accelerator

`ghacc` is an on-demand wrapper. It never changes global or repository Git configuration.

## Install

```bash
sudo install -m 0755 linux/ghacc.sh /usr/local/bin/ghacc
sudo install -m 0644 linux/ghacc.conf.example /etc/ghacc.conf
```

Edit `/etc/ghacc.conf` to put your preferred mirror first.

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

`latest` without an asset pattern lists Release assets. If acceleration fails, the script falls back to direct GitHub unless `GHACC_FALLBACK_DIRECT=0`.

## Private repositories

Authentication is delegated to your existing Git credential helper or SSH setup. Do not put tokens in `/etc/ghacc.conf`.