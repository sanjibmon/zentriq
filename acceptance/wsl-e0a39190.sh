set -e
curl -fsSLk --pinnedpubkey 'sha256//Lwt3xLr0TQrXigcBkc/tgU4t0tapgIWcGh9giMmsvHs=' https://localhost:8444/api/agent/v1/linux/install.sh | sudo sh -s -- --server https://localhost:8444 --code 80SF5-64SK2-88Q7K-W2Z1W --checkin 2 --inventory 30 --pin 'Lwt3xLr0TQrXigcBkc/tgU4t0tapgIWcGh9giMmsvHs='
zentriq-agent --identify
zentriq-agent --status || true