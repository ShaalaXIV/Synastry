# Aethercast Dalamud Repository

The combined custom-plugin repository is served at:

- `https://plugins.aethercast.org/`

`repo.json` contains the current AetherPress and Synastry store entries. The
repository is hosted by the `plugins-repo` Docker container. Its persistent
host files are under `/opt/portainer/plugins-repo`.

The live repository file is `/opt/portainer/plugins-repo/www/repo.json`. The
deployed Synastry icon is copied from `images/icon.png` to
`/opt/portainer/plugins-repo/www/images/synastry.png` and is served at
`https://plugins.aethercast.org/images/synastry.png`.

The `aethercast-cert-renew` systemd timer runs Certbot daily and validates and
reloads Nginx Proxy Manager afterward. Its service calls the deployed
`/usr/local/sbin/renew-aethercast-cert` script.
