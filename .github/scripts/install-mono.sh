#!/usr/bin/env bash
# net48 targets build and run on the Linux runner only under Mono.
set -euo pipefail

if dpkg-query -W -f='${Status}' mono-complete 2>/dev/null | grep -q 'install ok installed'; then
  echo "mono-complete already installed"
else
  sudo apt-get update
  sudo DEBIAN_FRONTEND=noninteractive apt-get install -y mono-complete
fi

mono --version | head -n 1
