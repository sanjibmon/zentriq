set -e
curl -fsSLk --pinnedpubkey 'sha256//Zam3Uop9dE4Ra05IwwHQAbPy/so16h/Xg/LbwggBxxM=' https://localhost:8444/api/agent/v1/linux/install.sh | sudo sh -s -- --server https://localhost:8444 --code ZNFHN-TJECS-98ZVH-KBS0Z --checkin 2 --inventory 30 --pin 'Zam3Uop9dE4Ra05IwwHQAbPy/so16h/Xg/LbwggBxxM='
zentriq-agent --identify
zentriq-agent --status || true