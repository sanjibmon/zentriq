set -e
curl -fsSLk --pinnedpubkey 'sha256//OIifqbEOt+rODQVTJmWucTLyxQ+v0HXxk2x9gwtf5Cg=' https://localhost:8444/api/agent/v1/linux/install.sh | sudo sh -s -- --server https://localhost:8444 --code CKRQY-2TMVT-4AAJ6-EHF6T --checkin 2 --inventory 30 --pin 'OIifqbEOt+rODQVTJmWucTLyxQ+v0HXxk2x9gwtf5Cg='
zentriq-agent --identify
zentriq-agent --status || true