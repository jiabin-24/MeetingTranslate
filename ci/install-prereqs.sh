#!/bin/bash
set -euo pipefail

echo "[install-prereqs] Start"

# Ensure pwsh exists
if command -v pwsh >/dev/null 2>&1; then
  echo "[install-prereqs] pwsh present: $(command -v pwsh)"
else
  echo "[install-prereqs] pwsh not present; continuing but PowerShell steps may fail"
fi

# On azure-cli image, az and bicep are expected to be present. Just report their status.
if command -v az >/dev/null 2>&1; then
  echo "[install-prereqs] az present: $(command -v az)"
else
  echo "[install-prereqs] az not found — installing azure-cli via Microsoft repo"
  if command -v apt-get >/dev/null 2>&1; then
    apt-get update -y
    apt-get install -y wget apt-transport-https ca-certificates gnupg curl
    wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
    dpkg -i /tmp/packages-microsoft-prod.deb || true
    apt-get update -y
    apt-get install -y azure-cli || echo "[install-prereqs] Failed to install azure-cli via apt"
  else
    echo "[install-prereqs] cannot install azure-cli (apt-get not available)"
  fi
fi

if command -v az >/dev/null 2>&1; then
  echo "[install-prereqs] running az bicep install if needed"
  az bicep install || echo "[install-prereqs] az bicep install failed"
fi

echo "[install-prereqs] bicep path: $(command -v bicep 2>/dev/null || echo not-found)"
echo "[install-prereqs] PATH=$PATH"

echo "[install-prereqs] Done"
