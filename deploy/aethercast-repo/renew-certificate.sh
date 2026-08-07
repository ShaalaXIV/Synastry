#!/bin/sh
set -eu

/usr/bin/docker exec npm-app-1 certbot renew --quiet
/usr/bin/docker exec npm-app-1 nginx -t
/usr/bin/docker exec npm-app-1 nginx -s reload
