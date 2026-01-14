#!/bin/bash
set -euo pipefail

echo "[install-prereqs] Start"

# Ensure pwsh exists
if ! command -v pwsh >/dev/null 2>&1; then
  echo "[install-prereqs] pwsh not found, attempting apt-based install"
  if command -v apt-get >/dev/null 2>&1; then
    apt-get update -y
    apt-get install -y wget apt-transport-https ca-certificates gnupg curl
    wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
    dpkg -i /tmp/packages-microsoft-prod.deb || true
    apt-get update -y
    apt-get install -y powershell || { echo "[install-prereqs] Failed to install powershell"; exit 1; }
  else
    echo "[install-prereqs] pwsh is not available and apt-get is missing; cannot install pwsh on this runner"; exit 1
  fi
else
  echo "[install-prereqs] pwsh present: $(command -v pwsh)"
fi

# On azure-cli image, az and bicep are expected to be present. Just report their status.
if command -v bicep >/dev/null 2>&1; then
  echo "[install-prereqs] bicep present: $(command -v bicep)"
else
  echo "[install-prereqs] bicep not found: $(command -v bicep 2>/dev/null || echo not-found)"
fi

if command -v az >/dev/null 2>&1; then
  echo "[install-prereqs] az present: $(command -v az)"
else
  echo "[install-prereqs] az not found: $(command -v az 2>/dev/null || echo not-found)"
fi

echo "[install-prereqs] PATH=$PATH"

echo "[install-prereqs] Done"
