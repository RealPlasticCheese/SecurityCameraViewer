# Camera authentication support

[Back to camera setup](camera-setup.md) · [Back to README](../README.md)

Authentication support varies between the RTSP connection check, video playback and ONVIF inspection. A successful RTSP `DESCRIBE` response confirms that the camera advertises video; the setup test separately checks that the selected decoder can receive a frame.

## Supported methods

| Method | RTSP connection check | Video playback | ONVIF inspection |
| --- | --- | --- | --- |
| Anonymous access | Supported | Supported when the camera allows it | Supported when the device allows it |
| Basic username/password | Supported | Handled by FFmpeg | HTTP authentication handled by .NET |
| MD5 Digest | Supported, including `MD5-sess` | Handled by FFmpeg | HTTP authentication handled by .NET |
| SHA-256 Digest | Supported, including `SHA-256-sess` | Depends on the installed FFmpeg version | Depends on .NET's HTTP authentication support; not implemented separately by this client |
| Digest without `qop` | Supported | Depends on the decoder | Handled by .NET for HTTP authentication |
| Digest with `qop=auth` | Supported | Depends on the decoder | Handled by .NET for HTTP authentication |
| WS-Security UsernameToken password digest | Not applicable | Not applicable | Supported; uses a random nonce and device UTC time when available |
| Token already present in an RTSP URL query | Passed through | Passed through | A returned RTSP URL can contain a token |

The ONVIF client combines HTTP authentication through `HttpClientHandler` with SOAP WS-Security when a username is supplied. HTTP authentication compatibility is determined by the .NET runtime and device challenge. This is not a claim that every authentication scheme exposed by .NET works with every camera.

Basic authentication does not encrypt credentials. Plain `rtsp://` and `http://` connections are unencrypted. An ONVIF `https://` endpoint uses normal certificate validation; the client does not bypass an invalid or self-signed certificate.

## Credential configuration and storage

- Each camera can have its own RTSP username and password, or use the shared recorder password.
- ONVIF and RTSP accounts may differ. Changing or clearing the RTSP password preserves the separate ONVIF credentials.
- Credentials are separate from RTSP URLs. URLs containing embedded `username:password@` credentials are rejected; credentials embedded in an ONVIF stream URI response are removed.
- The web server persists passwords in its private unified settings file on Windows and Linux and restores them after restart. See [password storage and file format](camera-setup.md#passwords-and-unattended-startup).
- Diagnostics omit credential fields and redact URL query values. Portable configuration exports retain exact URLs and must be reviewed separately.

## Not implemented / future work

| Method or behavior | Current limitation | Work required before claiming support |
| --- | --- | --- |
| Digest `qop=auth-int` | The RTSP probe rejects a challenge that only offers `auth-int`. | Implement entity-body hashing and test both probing and decoding. |
| SHA-512/256 Digest | Not implemented in the RTSP probe. | Add the algorithm and validate compatible FFmpeg versions. |
| WS-Security UsernameToken password text | The ONVIF client only generates password-digest tokens. | Add explicit configuration and tests for devices requiring password text. |
| OAuth, bearer tokens and vendor login flows | No token acquisition, authorization-header configuration, renewal or refresh workflow. | Add credential lifecycle handling and provider-specific interoperability tests. Existing query tokens are passed through without renewal. |
| Client certificates / mutual TLS | No client-certificate selection or storage for ONVIF. | Add certificate configuration and test certificate authentication. |
| WS-Security certificate signatures | Not implemented. | Add SOAP signing and the required key/certificate handling. |
| `rtsps://` encrypted RTSP | Source validation currently accepts `rtsp://` only. | Add TLS-aware probing, certificate validation and decoder interoperability tests. |
| Enterprise SSO, NTLM or Kerberos camera login | No dedicated configuration or tested end-to-end support. | Define supported runtime/device combinations and verify probing, ONVIF and playback as applicable. |
| Automatic credential renewal or rotation | Passwords and supplied URL tokens are static until updated. | Add an explicit renewal mechanism and failure/recovery behavior. |

These are documented extension points, not features enabled by this change. ONVIF redirects and advertised service endpoints on a different hostname are deliberately rejected before forwarding credentials. HTTPS services cannot downgrade to HTTP during inspection.

## Verification boundaries

Automated loopback tests cover RTSP Basic, legacy MD5 Digest, Digest `qop=auth`, SHA-256 Digest, rejected credentials, independent camera credentials, and ONVIF UsernameToken digest with clock skew.

The `-sess` variants are implemented but do not yet have dedicated interoperability tests. HTTP authentication at an ONVIF endpoint, HTTPS certificate handling, physical camera firmware and installed decoder variations need target-device testing. The ONVIF fixture verifies SOAP UsernameToken digest; it does not certify every HTTP authentication scheme. No ONVIF conformance certification is implied.
