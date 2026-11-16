# Private remote access with Tailscale

Tailscale Serve gives the JARVIS browser and Android audio companion a private HTTPS address. Serve runs on the Windows computer that hosts Core and proxies to its existing loopback listener on port 5186. Your tailnet policy limits access. Core's normal owner and device credentials still apply. [Tailscale Serve](https://tailscale.com/docs/reference/tailscale-cli/serve)

## Set up the tailnet

1. Install Tailscale on the Core computer, your Android phone, and your laptop. Sign all three into your tailnet and confirm they appear under **Machines**. Keep the Core computer awake when you want to connect. [Install Tailscale](https://tailscale.com/docs/install)
2. In the Tailscale admin console, open **DNS**, enable **MagicDNS** and **HTTPS Certificates**, and copy the Core machine's full `.ts.net` name. The HTTPS certificate lists this name in public certificate transparency records. Network access is still controlled by the tailnet. [Set up HTTPS certificates](https://tailscale.com/docs/how-to/set-up-https-certificates)
3. In **Access controls**, add a grant for TCP 443 from your phone and laptop to the Core machine. Replace the example addresses below with their Tailscale addresses. The source list contains both clients and the destination list contains Core, with IPv4 and IPv6 for each. Review existing wildcard ACLs and grants that also allow access to Core on 443. Permissions are additive, so narrow those rules to keep access limited to your two devices. [Tailscale grants](https://tailscale.com/docs/features/access-control/grants)

~~~json
{
  "grants": [
    {
      "src": ["100.70.10.11", "100.70.10.12", "fd7a:115c:a1e0::11", "fd7a:115c:a1e0::12"],
      "dst": ["100.70.10.20", "fd7a:115c:a1e0::20"],
      "ip": ["tcp:443"]
    }
  ]
}
~~~

## Start Serve on the Core computer

Open an administrator terminal. Check the existing listeners, then start the private reverse proxy on 443. Use Tailscale Serve for this setup. Funnel publishes a public endpoint.

~~~powershell
tailscale serve status
tailscale funnel status
tailscale serve --bg --https=443 https+insecure://127.0.0.1:5186
tailscale serve status
~~~

The `https+insecure` target is only for the self-signed TLS connection from Serve to Core on the same computer. Clients connect to the automatically provisioned `.ts.net` certificate with standard TLS validation. Keep the Compose host mappings for Core and PostgreSQL on loopback. [Tailscale Serve HTTPS options](https://tailscale.com/docs/reference/tailscale-cli/serve#use-https-and-http-servers)

To remove this Serve listener, run `tailscale serve --bg --https=443 off`. [Disable Tailscale Serve](https://tailscale.com/docs/reference/tailscale-cli/serve#disable-tailscale-serve)

## Connect the browser and Android

- Open the full `https://<core-machine>.<tailnet>.ts.net` URL on the laptop. Check `/health/ready` for HTTP 200, then use the dashboard with your usual Core login. The browser builds its WebSocket URLs from the page origin. Core authorizes dashboard hub connections with short-lived tickets and applies the same route policies over the proxy.
- Enrol the phone on Core and issue its own `jdv1_` device credential. In Android connection settings, enter the full HTTPS root URL and the device credential, then connect. Manual entry works on Android 0.10.2. Provisioning payloads in the updated app may omit the certificate fingerprint to use system trust. The ADB helper is for the pinned USB setup.
- Leave the certificate fingerprint unset for Serve. Android verifies the hostname and certificate chain for both HTTPS and voice WebSockets. A fingerprint explicitly supplied for USB pairing pins the exact leaf certificate and requires re-pairing when that certificate changes.

Core uses the authenticated actor for its request limit when available. Unauthenticated requests through a loopback proxy share the proxy address partition. Keep Core credentials and the tailnet's access rules in place.

## Test away from Wi-Fi

1. On the phone, turn off Wi-Fi and connect Tailscale over mobile data. Confirm the dashboard opens with the full HTTPS name and check that another tailnet device outside the grant cannot reach it.
2. Connect or re-pair Android, then verify the microphone and earbuds. If Core voice is configured, start a short live session and try **Hey JARVIS**.
3. Switch between Wi-Fi and mobile data during a live session. Confirm the phone recovers by starting a fresh Core connection.
4. If Home Assistant is configured, test a granted light and verify the resulting state. Stop audio when finished.

## Native Windows TLS alternative

Use this option when you need Core itself to terminate HTTPS on its Tailscale address. Set `Server:Windows:BindAddress` to the computer's tailnet IP and use the configured `Server:Windows:HttpsPort` in both the tailnet grant and the client URL. The Windows process binds its HTTP and HTTPS listeners to that address. For the Docker deployment, `Server:Container:BindAddress` is inside the container, while Compose controls which host addresses publish the ports. Use Serve with the existing loopback mappings for Docker.

On the Core computer, in a private directory, obtain the full hostname certificate and convert it to a password-protected PFX using OpenSSL. Substitute your own hostname.

~~~powershell
tailscale cert jarvis.example-tailnet.ts.net
openssl pkcs12 -export -in jarvis.example-tailnet.ts.net.crt -inkey jarvis.example-tailnet.ts.net.key -out jarvis-tailnet.pfx
~~~

Set `Server:Windows:CertificatePath` to the PFX path, `Server:Tls:Password` to its password, and `Server:Tls:Enabled` to `true` in private settings. Restart Core and check HTTPS from an allowed device. Renew and replace manually exported certificates before expiry. Keep the certificates, keys, and passwords in private files. [Tailscale certificate renewal](https://tailscale.com/docs/how-to/set-up-https-certificates#provision-tls-certificates-for-your-devices)

The updated Core accepts loopback, private LAN, and tailnet addresses by default. `Server:Windows:AllowPublicBind` and `Server:Container:AllowPublicBind` are explicit overrides for other addresses. Keep them false for the private setup. The standard Docker IPv4 wildcard remains available inside the container, with Compose publishing Core on host loopback.
