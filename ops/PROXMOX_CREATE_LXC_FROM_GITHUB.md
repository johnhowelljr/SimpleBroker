# Proxmox: create a fully working LXC broker from GitHub compiled artifacts

This script runs on the **Proxmox host** and creates a Debian 12 LXC container, installs the SimpleEndpointBus broker, and enables it as a systemd service.

## One-time: publish and attach an artifact to a GitHub Release

On your build machine (or CI):

```bash
bash scripts/publish-linux.sh linux-x64
tar -C publish/broker/linux-x64 -czf seb-broker-linux-x64.tar.gz .
```

Upload `seb-broker-linux-x64.tar.gz` to a GitHub Release (tag `vX.Y.Z` or use `latest`).

## Run on Proxmox host

Copy this script to the Proxmox host and run as root:

```bash
chmod +x ops/create-seb-broker-lxc-from-github.sh
./ops/create-seb-broker-lxc-from-github.sh
```

The script will prompt for:
- CTID, hostname, storage/disk, CPU, memory
- DHCP vs static IP
- GitHub repo + release tag (`latest` ok) + asset filename
- Optional GitHub token (private repo or to avoid rate limits)

## Verify

```bash
curl http://<CT_IP>:8080/health
```

## Logs

```bash
pct exec <CTID> -- journalctl -u simple-endpoint-bus-broker -f
```
