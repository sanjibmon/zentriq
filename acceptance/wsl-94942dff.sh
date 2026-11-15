set -e
dpkg-query -W -f='${Status}' zq-ci-dummy 2>/dev/null || echo gone