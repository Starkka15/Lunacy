#!/bin/sh
# Prints Lunacy's log off the phone: ./getlog.sh <version>
V=${1:?version}; PH=${W10M_PHONE:-192.168.5.47}
J="$(cygpath "$LOCALAPPDATA")/W10MLuna/portal.cookies"
curl -sk --noproxy '*' -b "$J" -c "$J" "https://$PH/api/filesystem/apps/file?knownfolderid=LocalAppData&packagefullname=Lunacy_${V}_arm__ngbftj7js51be&filename=lunakit.log&path=%5CLocalState"
