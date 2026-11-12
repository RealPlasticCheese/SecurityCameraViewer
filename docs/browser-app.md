# Open the viewer as a desktop app

For a computer that normally connects to an existing server, install the site as a web app once and pin its shortcut to the taskbar or desktop. It opens the same browser UI in its own window and needs only the browser on that computer. The server must remain running and reachable.

In Edge, open the server address, then use **Settings and more > More tools > Apps > Install this site as an app** (wording varies by version). In Chrome, use the browser's **Install page as app** option or the viewer's **Install app** menu item when offered. Prefer the trusted HTTPS address from [the HTTPS setup guide](https-linux-nuc.md) for remote installation. See [Microsoft's web app guide](https://support.microsoft.com/en-us/edge/install-manage-or-uninstall-apps-in-microsoft-edge) and [Chrome's web app instructions](https://support.google.com/chrome/answer/9658361).

A launch script is useful when you want to choose an address each time, or open a plain HTTP LAN address without installing a web app.

## Start a local server and open its app window

Windows, from the checkout:

```powershell
.\launch-web.cmd --open-browser
.\launch-web.cmd --open-browser --browser chrome
```

Linux, from a graphical desktop session:

```bash
bash run-web.sh --open-browser
bash run-web.sh --open-browser --browser chromium
```

The published executable or DLL accepts the same options:

```bash
dotnet artifacts/web/SecurityCameraViewer.Web.dll --open-browser
```

The browser opens once, after the server is listening. The launch uses the actual listening port, including an OS-assigned port when using `--urls http://127.0.0.1:0`. Wildcard bindings (`0.0.0.0`, `[::]`, `*`, or `+`) become `localhost` in the browser address. Other bindings retain their host, scheme and port. When multiple addresses are bound, the first usable address is selected.

Closing the browser window leaves the server running. Stop the server with **Ctrl+C**. Browser launching defaults to off, so existing headless and systemd installations keep their startup behavior. Launch failures are reported without stopping a successfully started server.

If you use a local HTTPS reverse proxy, select its address explicitly:

```bash
bash run-web.sh --open-browser --browser-url https://viewer.example/
```

An app window hides the usual tab strip and address bar. It still uses the browser's existing profile, permissions, cookies and browser storage. An app window launched this way does not install a web app or create a shortcut.

## Connect to an existing server

Windows:

```powershell
.\connect-web.cmd "http://camera-server:5080/"
.\connect-web.cmd "https://viewer.example/" --browser edge
```

Linux:

```bash
bash connect-web.sh "http://camera-server:5080/"
bash connect-web.sh "https://viewer.example/" --browser chromium
```

These scripts open a browser and exit. They do not start a server, read camera settings, connect cameras, or require FFmpeg on the viewing computer. The address must include `http://` or `https://`; include a custom port if needed. Use browser sign-in for authenticated installations rather than putting credentials into the URL. The browser reports connection or certificate problems normally.

The scripts use the existing published viewer runtime, building it if needed when run from the checkout. A first build needs the .NET 10 SDK; a framework-dependent publish needs the .NET 10 ASP.NET Core Runtime. The scripts also accompany published output, where they work beside the server executable/DLL. To avoid those dependencies on a viewing computer, use the installed web app shortcut described above.

The equivalent direct command is:

```bash
dotnet artifacts/web/SecurityCameraViewer.Web.dll --connect https://viewer.example/
```

Connection mode exits with a nonzero status for invalid options or a detected launch failure. It does not wait for the browser to close or check server availability.

## Browser selection

| Option | Behavior |
| --- | --- |
| `--browser auto` (default) | Windows tries Edge, Chrome, Chromium, then Brave. Linux tries Chrome, Chromium, Edge, then Brave. If no app browser can be launched, reports the fallback and opens the OS default browser in a regular window. |
| `--browser edge`, `chrome`, `chromium`, or `brave` | Opens the selected Chromium browser with `--app=<address>`. Discovery checks PATH and, on Windows, standard per-user and system installation directories. Missing or unlaunchable explicit selections report an error. |
| `--browser default` | Opens the address normally in the OS default browser, including Firefox. |
| `--browser-path "path to executable"` | Uses a custom Chromium browser executable (full/relative path or executable name on PATH) with the same app switch. Cannot be combined with `--browser default`. |

Chrome and Chromium define the [application mode command-line switch](https://chromium.googlesource.com/chromium/chromium/+/142dbc0336a678cde9069b7da4e4808e0c4b2da1/chrome/common/chrome_switches.cc). Edge also supports application mode; Microsoft's [known issues list](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-known-issues) documents it. Brave uses Chromium's app switch. Other Chromium variants can be selected with `--browser-path`; whether they honor the switch depends on that browser.

[Firefox supports installed web apps on Windows](https://support.mozilla.org/en-US/kb/web-apps-firefox-windows), starting with version 143 (version 150 for Microsoft Store installations). Its web app window retains a read-only address bar. Mozilla currently documents this feature for Windows only. Use Firefox's web app installation button to create its shortcut; this launcher does not pass Chromium's app switch to Firefox.

A Linux launch requires a graphical X11 or Wayland session (`DISPLAY` or `WAYLAND_DISPLAY`). A system service started before desktop login cannot open an app window; install/pin a web app on the viewing desktop instead.

## Validation

Run the browser unit checks and real-server startup/connection checks after publishing:

```powershell
dotnet run --project tests/SecurityCameraViewer.Web.Tests -c Release -- --browser-launch-only artifacts/web/SecurityCameraViewer.Web.dll
python tests/test_connect_web.py --bash "C:/Program Files/Git/bin/bash.exe"
```

On Linux, run `python3 tests/test_connect_web.py`; it selects Bash from PATH. Browser tests use a capture executable, so they do not open user browser windows. They verify argument handling, Windows/Linux discovery, fallback and failure reporting, startup/shutdown timing, real bound addresses and existing-server connection without loading settings. Script tests mock the viewer executable/runtime and first-build behavior.
