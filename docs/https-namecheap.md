# Trusted HTTPS on the NUC with Namecheap and Let's Encrypt

Use this guide for **https://viewer.example.com:5080/** on the NUC at
`192.168.1.50`. It provides a publicly trusted certificate and automatic
renewal. Phones do not need a custom CA certificate. The viewer remains accessible
only where its private LAN address is reachable; no router port forwarding is needed.

The Linux Mint/Ubuntu setup uses the distribution's Caddy and lego packages.
Caddy forwards HTTPS to the existing viewer. Lego obtains and renews the certificate
using Namecheap DNS validation. Caddy listens on your chosen HTTPS port and connects
to the camera app on a separate internal HTTP port. Use the hostname for public trust.
Opening **http://viewer.example.com/** redirects permanently (HTTP 308) to
**https://viewer.example.com:5080/** after selecting HTTPS port 5080.
Both **http://viewer.example.com/** (port 80) and
**http://viewer.example.com:5080/** redirect to HTTPS on 5080, preserving
paths and query strings. Caddy's built-in `http_redirect` listener wrapper detects
plain HTTP on the HTTPS port and redirects it before TLS handling.
[Caddy listener wrapper documentation](https://caddyserver.com/docs/caddyfile/options#http-redirect).

If trusted HTTPS is already installed, skip to **Change the HTTPS port** below.

## 1. Prepare the hostname

In Namecheap, open **Domain List → your domain → Manage → Advanced DNS**.
Create or retain this record, then save it:

| Type | Host | Value | TTL |
|---|---|---|---|
| A | camera-host | 192.168.1.50 | Automatic |

Reserve `192.168.1.50` for the NUC in the router's DHCP settings.
On the NUC, check:

```bash
getent ahostsv4 viewer.example.com
curl --fail http://127.0.0.1:5080/manifest.webmanifest
```

The first command must show `192.168.1.50`; the second must return the app manifest.
If the viewer is not installed, follow the [Linux service instructions](../README.md#linux-service-run-automatically-at-startup)
first. Initial HTTPS installation does not rebuild or restart the camera application.
The port migration command restarts its service to change the HTTP listener.

## 2. Enable Namecheap API access

1. Sign into Namecheap and open **Profile → Tools**.
2. Under **Business & Dev Tools**, open **Namecheap API Access → Manage**.
3. Enable **production** API access. Your account must meet Namecheap's eligibility
   requirements, or support must grant access. Sandbox credentials cannot issue this certificate.
4. Note your Namecheap account username and API key. Enter them only in the NUC's
   setup prompts; do not commit them or put them into chat or shell command arguments.
5. On the NUC, obtain the public outbound IPv4 address:

   ```bash
   curl -4 --fail --silent --show-error https://api.ipify.org
   printf '\n'
   ```

6. Add that address under **Namecheap API Access → Whitelisted IPs → Edit → Add IP**.
   Use the public address printed above, rather than `192.168.1.50`.

See [Namecheap API setup and allowlisting](https://www.namecheap.com/support/api/intro/)
and [lego's Namecheap provider](https://go-acme.github.io/lego/dns/namecheap/).
The DNS API updates the domain's records to add/remove the temporary challenge TXT
record. Avoid editing that domain's DNS records concurrently with issuance or renewal.

## 3. Run setup on the NUC

Update your repository to a version containing `web-https.sh`, then open a terminal
in the project folder. Run this block:

```bash
read -r -p 'Certificate renewal contact email: ' acme_email
sudo bash web-https.sh install viewer.example.com "$acme_email"
```

Enter your Linux sudo password, Namecheap username, and Namecheap API key when prompted.
The API key is hidden while you type. Running install accepts the
[Let's Encrypt subscriber agreement](https://letsencrypt.org/repository/).
Setup prompts for **Internal camera app HTTP port** (default 5080) and
**Browser HTTPS port** (default 443). These must be different. For an existing
viewer on HTTP 5080, accept those initial values, then use the migration command
below to put HTTPS on 5080 and move the app's HTTP listener to loopback 5081.
For scripted installation, pass backend and HTTPS ports after the email:

```bash
sudo bash web-https.sh install viewer.example.com "$acme_email" 5080 443
```

Setup installs dependencies, checks the existing viewer, obtains a production
certificate, validates the configuration as Caddy's service account, and enables a
daily renewal timer. DNS validation may take several minutes. It opens HTTP port 80 and the chosen HTTPS port if
UFW is already active; it does not enable UFW or configure router forwarding.
Do not interrupt the command while it is issuing the certificate.

Success includes:

```text
HTTPS verified: https://viewer.example.com:443/
```

The managed site is `/etc/caddy/security-camera-viewer-acme.caddy`, imported by the
existing Caddyfile. Validation/reload failures restore the previous serving configuration
and certificates. Existing Caddy sites are preserved. Unmarked files at the script's
managed paths are refused instead of overwritten.
Listener options are stored separately in `/etc/caddy/security-camera-viewer-listener.caddy`
and imported inside the Caddyfile's global options block. Existing global options
are preserved, and the wrapper applies only to the selected HTTPS listener.

### Change the HTTPS port

After the initial HTTPS installation, run:

```bash
sudo bash web-https.sh ports
```

The script prompts for:

1. **Browser HTTPS port [5080]**: press Enter for 5080.
2. **Internal camera app HTTP port [5081]**: press Enter for 5081.

It preserves the existing certificate, API credentials, camera settings, and startup
arguments. The camera service restarts with HTTP bound to **127.0.0.1:5081**, reachable
only from the NUC. Caddy serves **https://viewer.example.com:5080/**. The renewal
service remembers both ports. The old IP-based site's import is disabled, with its
configuration retained on disk. The camera app no longer serves HTTPS on 443; other
unrelated Caddy sites, if any, keep their configuration. Existing firewall rules are
left in place; the selected HTTPS port is allowed if UFW is active.

Success includes `HTTPS verified: https://viewer.example.com:5080/` and
`HTTP redirect verified: http://viewer.example.com/ -> https://viewer.example.com:5080/`.
For noninteractive use, pass HTTPS port followed by backend port:

```bash
sudo bash web-https.sh ports 5080 5081
```

The migration rejects occupied ports, unmanaged service units, and service overrides
that require manual review. On failure it restores the original service, proxy, and
saved port settings. The brief service restart interrupts live connections; browsers
can reconnect afterwards.

## 4. Verify and open the app

From the NUC:

```bash
curl --fail https://viewer.example.com:5080/manifest.webmanifest
curl --silent --show-error --output /dev/null --dump-header - 'http://viewer.example.com/manifest.webmanifest?camera=1'
curl --silent --show-error --output /dev/null --dump-header - 'http://viewer.example.com:5080/manifest.webmanifest?camera=1'
systemctl is-active caddy security-camera-viewer.service
systemctl is-enabled caddy security-camera-viewer-https.timer
systemctl list-timers security-camera-viewer-https.timer --no-pager
```

Expect manifest JSON, two `active` lines, two `enabled` lines, and a scheduled timer.
Both HTTP requests must return status **308** with a `Location` of
`https://viewer.example.com:5080/manifest.webmanifest?camera=1`.
On a phone connected to home Wi-Fi, open **https://viewer.example.com:5080/** in
Chrome. It should open without a certificate warning or a CA installation step.
Sign in if required. Use **Install app** in the viewer's hamburger menu or Chrome's
menu when available. If you installed the app from the old IP address, install it
again from the new hostname and port. Website sessions and app installations are tied to their origin.

## 5. Renewal and maintenance

The timer runs daily, including missed runs after startup. Lego renews when fewer
than 30 days remain. The renewal service reloads Caddy after installing the current
certificate. It uses a root-owned script independent of your source checkout:

```bash
sudo systemctl start security-camera-viewer-https.service
sudo journalctl -u security-camera-viewer-https.service -n 50 --no-pager
```

A manual run checks renewal; it does not force an unnecessary new certificate.
If your public IP changes, update Namecheap's IP allowlist, then run the service again.
If you rotate your API key or change settings, rerun the install command and enter the
new credentials. To update the installed renewal script after a repository update,
run `sudo bash web-https.sh refresh`. It reapplies saved settings and updates the
installed renewal script without asking for API credentials, issuing a new certificate,
or restarting the camera application. This also adds the HTTP redirect to an existing
HTTPS installation. To change credentials, rerun install instead, selecting your current
backend and HTTPS ports at the prompts.
To change ports without re-entering credentials, use `sudo bash web-https.sh ports`.

Credentials are stored in `/etc/security-camera-viewer-https/settings` with mode 0600.
ACME account data and original certificate keys are root-only under
`/var/lib/security-camera-viewer-https`. Caddy gets a separate certificate/key copy in
`/etc/caddy/security-camera-viewer-tls`, readable only by root and the Caddy group.
Keep these files outside Git and preserve them securely when backing up the NUC.

| Problem | Action |
|---|---|
| API access denied | Check production credentials, eligibility, and the current public IPv4 allowlist entry. Rerun install after correcting these. |
| DNS validation fails | Check DNS propagation and any CAA policy permitting Let's Encrypt. Inspect the error before retrying; repeated production attempts can hit CA rate limits. |
| Hostname fails on one device | Check that it resolves to `192.168.1.50`. Router DNS rebinding protection may need an exception for this hostname. Guest Wi-Fi may block LAN access. |
| HTTPS has a certificate warning | Use the hostname rather than the IP. Check the device clock and renewal service logs. Do not bypass the warning. |
| Plain HTTP on 5080 reports an HTTP request to an HTTPS server | Update `web-https.sh` and run `sudo bash web-https.sh refresh` to enable the same-port listener wrapper. Confirm `/etc/caddy/security-camera-viewer-listener.caddy` is imported inside the global options block. |
| 502 response | Check the camera service and backend port; rerun install with the correct port. |
| Renewal fails after an internet reconnect | Update Namecheap's allowlisted public IPv4 address and rerun the renewal service. The existing certificate continues working until expiry. |
| Caddy validation fails | Inspect `sudo journalctl -u caddy -n 50 --no-pager`; resolve duplicate hostname sites, custom service overrides, or port conflicts. |

For the alternative that uses only a private IP and a manually trusted local CA,
see [the local HTTPS guide](https-linux-nuc.md).
