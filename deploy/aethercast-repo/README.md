# Aethercast Dalamud Repository

The combined custom-plugin repository is served at both:

- `https://aethercast.org/repo`
- `https://aethercast.org/repo/repo.json`

`repo.json` contains the current AetherPress and Synastry store entries. The
Nginx files are loaded through Nginx Proxy Manager's supported
`/data/nginx/custom/http.conf` include.

The deployed Synastry icon is copied from `images/icon.png` to
`/data/aethercast-repo/images/synastry.png` in the proxy container's data
volume.

The `aethercast-cert-renew` systemd timer runs Certbot daily and validates and
reloads Nginx Proxy Manager afterward. Its service calls the deployed
`/usr/local/sbin/renew-aethercast-cert` script.
