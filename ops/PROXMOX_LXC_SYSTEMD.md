# Proxmox LXC deployment (systemd)

This is the simplest way to run the broker in an LXC container: publish a self-contained binary and run it under systemd.

## 1) Publish the broker (on any Linux build machine)
From the repo root:

```bash
bash scripts/publish-linux.sh linux-x64
```

This produces:

```text
publish/broker/linux-x64/SimpleEndpointBus.Broker
```

## 2) Copy files into the LXC
Copy the published broker folder into the LXC (use `scp`, `rsync`, or mount a share):

```bash
sudo mkdir -p /opt/simple-endpoint-bus/broker
sudo cp -a publish/broker/linux-x64/* /opt/simple-endpoint-bus/broker/
sudo chmod +x /opt/simple-endpoint-bus/broker/SimpleEndpointBus.Broker
```

## 3) Create a service user
```bash
sudo useradd --system --home /nonexistent --shell /usr/sbin/nologin seb || true
sudo chown -R seb:seb /opt/simple-endpoint-bus/broker
```

## 4) Install the systemd unit + env file
Templates are in `ops/`:

```bash
sudo cp ops/simple-endpoint-bus-broker.service /etc/systemd/system/simple-endpoint-bus-broker.service
sudo cp ops/simple-endpoint-bus-broker.env /etc/default/simple-endpoint-bus-broker
```

Edit the env file and set `SEB_BaseUri` to the LXC's LAN IP:

```bash
sudo nano /etc/default/simple-endpoint-bus-broker
```

Example:
```text
SEB_BaseUri=http://10.1.1.50:8080
```

If you have multiple subnets, add peer brokers:
```text
SEB_Peers=http://10.1.2.50:8080;http://10.1.3.50:8080
```

## 5) Start the service
```bash
sudo systemctl daemon-reload
sudo systemctl enable --now simple-endpoint-bus-broker
sudo systemctl status simple-endpoint-bus-broker --no-pager
```

## 6) Verify
```bash
curl http://<LXC_IP>:8080/health
```

## Firewall / ports
- TCP **8080** (broker API)
- UDP **37777** (LAN discovery)

Ensure your Proxmox/LXC firewall rules allow UDP 37777 if you use discovery.

## Logs
```bash
journalctl -u simple-endpoint-bus-broker -f
```
