#!/bin/sh
# Launches Lunacy on the phone through the Device Portal: ./launch.sh <version>
V=${1:?version}; PH=${W10M_PHONE:-192.168.5.47}
J="$(cygpath "$LOCALAPPDATA")/W10MLuna/portal.cookies"
c() { curl -sk --noproxy '*' -b "$J" -c "$J" "$@"; }
c -o /dev/null "https://$PH/"
T=$(grep -P '\tCSRF-Token\t' "$J" | tail -1 | awk '{print $NF}')
A=$(printf 'Lunacy_ngbftj7js51be!App' | base64 -w0)
P=$(printf "Lunacy_${V}_arm__ngbftj7js51be" | base64 -w0)
c -o /dev/null -w 'launch %{http_code}\n' -H "X-CSRF-Token: $T" -H "Content-Length: 0" -X POST "https://$PH/api/taskmanager/app?appid=$A&package=$P"
