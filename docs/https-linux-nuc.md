# HTTPS on the Linux NUC, then install on Android

For a publicly trusted certificate at **https://viewer.example.com:5080/** with
automatic Let's Encrypt renewal and no phone CA installation, follow
[the Namecheap HTTPS setup](https-namecheap.md). The guide below is the alternative
for accessing a private IP directly using Caddy's local certificate authority.

Follow these steps in order. This guide assumes Linux Mint or Ubuntu on the NUC, the viewer using its normal port **5080**, and your Android phone connected to the same home network. You need the viewer's existing .NET 10 SDK and FFmpeg installation, plus permission to run `sudo`.

Caddy will provide `https://YOUR-NUC-IP/` and forward requests to the viewer on port 5080. Its local certificate authority gives the phone a certificate to trust once. No public domain or router port forwarding is needed. Caddy renews the viewer certificate automatically. [Caddy local HTTPS](https://caddyserver.com/docs/automatic-https#local-https)

Keep the **same terminal window** open for steps 1–7 so the address variables stay available. Paste one code block at a time and wait for it to finish. If a command fails, use the troubleshooting table below before continuing.

## 1. Open the updated project on the NUC

Copy or update the project on the NUC to the version containing these changes. In the NUC's file manager, open the **SecurityCameraViewer** folder, right-click an empty space, and select **Open in Terminal**.

Check that this is the updated checkout:

```bash
ls src/SecurityCameraViewer.Web/wwwroot/app-install.js
```

It must print that filename. If it says the file does not exist, update the checkout first.

If a viewer is running in another terminal, stop that copy with **Ctrl+C**. Then publish the updated viewer and install/update its boot service:

```bash
if systemctl is-active --quiet security-camera-viewer.service; then
    bash web-service.sh stop
fi
bash build-web.sh && bash web-service.sh install
```

Enter your Linux password when asked. Check the running server:

```bash
curl --fail --silent --show-error http://127.0.0.1:5080/api/state
```

Expected: JSON describing the viewer. If you already enabled API authentication, a **401** here means the server is running; sign in from the browser later. Other connection errors need fixing before step 2. The service retains your saved camera configuration; use your existing password file or startup settings as described in the [main README](../README.md#linux-service-run-automatically-at-startup).

## 2. Find and save the NUC's address

```bash
nuc_ip=$(ip -4 route get 1.1.1.1 | awk '{for (i=1; i<=NF; i++) if ($i == "src") {print $(i+1); exit}}')
viewer_url="https://${nuc_ip}"
printf '\nViewer address: %s/\n' "$viewer_url"
```

Write down the printed address, for example **https://192.168.1.50/**. This address has **no `:5080`** on the end.

In your router's app or web page, find its connected devices, select the NUC, and enable **Reserve IP**, **Address reservation** or **Static lease** for this address. Keep the NUC's current address. The wording depends on the router; this stops the app's address changing after a reboot. If the printed address belongs to a VPN, disconnect the VPN and repeat this step using the NUC's home network connection.

## 3. Install Caddy

Paste this block. It installs the official stable Caddy package and its boot service:

```bash
sudo apt update
sudo apt install --yes debian-keyring debian-archive-keyring apt-transport-https curl gpg ca-certificates
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' -o /tmp/security-camera-viewer-caddy.gpg.key
sudo gpg --batch --yes --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg /tmp/security-camera-viewer-caddy.gpg.key
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' -o /tmp/security-camera-viewer-caddy.list
sudo install -m 0644 /tmp/security-camera-viewer-caddy.list /etc/apt/sources.list.d/caddy-stable.list
sudo chmod o+r /usr/share/keyrings/caddy-stable-archive-keyring.gpg
sudo apt update
sudo apt install --yes caddy
```

These commands follow [Caddy's Debian/Ubuntu installation instructions](https://caddyserver.com/docs/install#debian-ubuntu-raspbian). The two downloads are a repository signing key and package-source list.

## 4. Add the HTTPS viewer configuration

Paste this entire block, including the two `EOF` lines:

```bash
sudo cp -a /etc/caddy/Caddyfile "/etc/caddy/Caddyfile.backup.$(date +%Y%m%d-%H%M%S)"
sudo tee /etc/caddy/security-camera-viewer.caddy >/dev/null <<EOF
${viewer_url} {
    tls internal
    reverse_proxy 127.0.0.1:5080
}
EOF
if ! sudo grep -Fxq 'import /etc/caddy/security-camera-viewer.caddy' /etc/caddy/Caddyfile; then
    printf '\nimport /etc/caddy/security-camera-viewer.caddy\n' | sudo tee -a /etc/caddy/Caddyfile >/dev/null
fi
sudo caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
```

Expected: **Valid configuration**. Then activate it:

```bash
sudo systemctl enable --now caddy
sudo systemctl reload caddy
systemctl is-active caddy security-camera-viewer.service
```

Expected: two lines saying **active**. The configuration adds a separate viewer file and keeps the existing Caddyfile. Caddy handles WebSockets and forwards the HTTPS scheme; this viewer accepts those forwarding headers only from `127.0.0.1` or `::1`. [Caddy reverse proxy](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy#defaults), [ASP.NET Core proxy configuration](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0)

## 5. Allow HTTPS through the NUC's firewall

```bash
sudo ufw status
```

If it says **Status: active**, run:

```bash
sudo ufw allow 443/tcp comment 'Security Camera Viewer HTTPS'
```

If it says **inactive**, or `ufw` is not installed, skip that command. Leave the router's port-forwarding settings alone; this setup uses your home network.

## 6. Copy the phone's certificate and check HTTPS

Copy only the public root certificate into your NUC's Downloads folder:

```bash
mkdir -p "$HOME/Downloads"
sudo install -m 0644 -o "$(id -un)" \
    /var/lib/caddy/.local/share/caddy/pki/authorities/local/root.crt \
    "$HOME/Downloads/security-camera-viewer-ca.crt"
```

Now check the exact HTTPS address using that certificate:

```bash
curl --cacert "$HOME/Downloads/security-camera-viewer-ca.crt" \
    --fail --silent --show-error "$viewer_url/manifest.webmanifest"
```

Expected: JSON containing **Security Camera Viewer** and **standalone**. Do not continue if curl reports a certificate error. The certificate location is documented in [Caddy's systemd guide](https://caddyserver.com/docs/running#local-https-with-systemd).

## 7. Trust HTTPS on the NUC too

```bash
sudo install -m 0644 "$HOME/Downloads/security-camera-viewer-ca.crt" \
    /usr/local/share/ca-certificates/security-camera-viewer-caddy.crt
sudo update-ca-certificates
printf '\nOpen this on the phone: %s/\n' "$viewer_url"
```

This adds the certificate to Linux's system trust store. A browser with its own certificate store may also need an import under its certificate settings. The phone has its own trust store; continue below even if the NUC browser already works.

## 8. Put the certificate on the phone

1. Connect the phone to the NUC with a USB cable.
2. Unlock the phone. Tap its USB notification and choose **File transfer**.
3. On the NUC, open **Files → Downloads**.
4. Copy **security-camera-viewer-ca.crt**.
5. Open the phone in the Files sidebar, then **Internal storage → Download**, and paste the file.
6. Disconnect the cable after copying finishes.

Copy the `.crt` file only. Keep Caddy's `root.key` and other private keys on the NUC.

## 9. Install the certificate on Android

1. Open the phone's **Settings**.
2. Search Settings for **CA certificate** or **Encryption & credentials**.
3. Open **Install a certificate → CA certificate**. On Pixel, this is under **Security & privacy → More security settings → Encryption & credentials**. On some phones, it is under **Other security settings → Install from device storage**.
4. Android shows a trust warning because you are adding your NUC's certificate authority. Choose **Install anyway** for the certificate you just copied, and enter your phone's PIN if requested.
5. Choose **Download → security-camera-viewer-ca.crt**.
6. If asked for a name, use **Security Camera Viewer NUC**.

Use **CA certificate**, rather than the Wi-Fi or VPN certificate option. Menu wording varies with Android version and phone manufacturer. [Google's certificate instructions](https://support.google.com/pixelphone/answer/2844832?hl=en)

## 10. Open and install the viewer

1. Connect the phone to your home Wi-Fi.
2. Open **Chrome** in a normal tab.
3. Type the **https://.../** address printed in step 7. Do not add `:5080`.
4. Confirm that the page opens **without a certificate warning**. Bypassing a warning does not establish the trusted HTTPS connection needed here.
5. If authentication is enabled, open the hamburger menu, choose **Sign in**, and enter your administrator token. HTTP and HTTPS sessions are separate, so an old HTTP sign-in may not carry over.
6. Tap the hamburger menu once and leave the viewer open for at least **30 seconds**.
7. Scroll to the bottom of the hamburger menu and tap **Install app** when it appears.
8. Complete Chrome's installation prompt.
9. Open **Camera Viewer** from the phone's home screen or app drawer. It opens in its own window.

If the menu option has not appeared, use **Chrome ⋮ → Install app** or **Add to Home screen**. Browser engagement checks can delay the in-app option. [Chrome installation criteria](https://web.dev/articles/install-criteria)

Open the hamburger menu again: **Full screen** is beside **SECURITY CAMERA VIEWER**, aligned to the right. **Keep screen awake** is also available over trusted HTTPS.

## 11. Check that it survives a reboot

Reboot the NUC normally. Wait for it to reconnect to the network, then reopen **Camera Viewer** on the phone. Caddy and the viewer start automatically. The NUC must be on and reachable for live video and recordings.

You do not need to reinstall the phone's certificate when Caddy renews a viewer certificate. Retain `/var/lib/caddy/.local/share/caddy` when backing up or migrating the NUC: replacing its certificate authority requires trusting the new root on the phone again.

## Troubleshooting

| What you see | What to do |
|---|---|
| The phone cannot connect | Confirm it is on the same home Wi-Fi, using the reserved NUC IP with `https://` and no `:5080`. Check `systemctl is-active caddy security-camera-viewer.service` and step 5. Guest Wi-Fi can block access to LAN devices. |
| Caddy will not start or reload | Run `sudo journalctl -u caddy -n 50 --no-pager`. An “address already in use” message usually means another server is using port 80 or 443. A duplicate site message means that address already exists in your Caddy configuration. Resolve that conflict before retrying step 4. |
| Root certificate file is missing | Confirm Caddy is active and the reload succeeded. Retry `curl --insecure --silent --output /dev/null "$viewer_url/"` on the NUC once to trigger local certificate issuance, then repeat step 6. This diagnostic command is only for the NUC; trust the certificate before browser use. |
| Certificate warning on the phone | Install this NUC's root as a **CA certificate**, check the phone's date/time, close all Chrome windows and reopen Chrome. Use the exact IP from step 2. If you rebuilt Caddy's storage, repeat steps 6, 8 and 9 with the new root. |
| HTTPS says **502 Bad Gateway** | The viewer is not reachable on port 5080. Run `bash web-service.sh status` in the project folder; start it with `bash web-service.sh start`. If you deliberately use another port, update `reverse_proxy 127.0.0.1:5080` in `/etc/caddy/security-camera-viewer.caddy` and reload Caddy. |
| Page opens, but controls return **403** or video does not connect | Rebuild and restart the updated viewer from step 1. Its forwarding-header support is required for a local HTTPS proxy. Keep the proxy target at `127.0.0.1`, preserve the browser's Host header, and do not add another proxy hop. |
| **Install app** does not appear | Confirm trusted HTTPS, a normal Chrome tab, a tap and 30 seconds on the page. Try Chrome's own install menu. The in-app option stays hidden when Chrome has no prompt to offer or the viewer is already running as an app. |
| NUC IP changed | Reserve its new IP in the router, repeat steps 2 and 4, then use the new HTTPS address. If the old installed app still opens the old address, uninstall that app and install it from the new address. The CA stays the same if Caddy's storage is retained. |

## Updating the viewer later

In the project folder, after updating its source:

```bash
bash web-service.sh stop
bash build-web.sh && bash web-service.sh start
```

Caddy and the phone's certificate stay in place. Reopen the app to load the updated browser files.
