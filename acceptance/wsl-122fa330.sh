set -e
apt-get remove -y zentriq-agent >/tmp/rm.log 2>&1 && echo REMOVED || echo REFUSED; grep -m1 "is protected" /tmp/rm.log || tail -5 /tmp/rm.log