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
  echo "[install-prereqs] az not found — attempting official InstallAzureCLIDeb script"
  # ensure curl or wget exists; if not, try to install them via apt-get
  if ! command -v curl >/dev/null 2>&1 && ! command -v wget >/dev/null 2>&1; then
    echo "[install-prereqs] curl/wget not found - attempting to install via apt-get"
    if command -v apt-get >/dev/null 2>&1; then
      if [ "$(id -u)" -ne 0 ] && command -v sudo >/dev/null 2>&1; then
        sudo apt-get update -y
        sudo apt-get install -y curl wget || echo "[install-prereqs] Failed to install curl/wget via apt"
      else
        apt-get update -y
        apt-get install -y curl wget || echo "[install-prereqs] Failed to install curl/wget via apt"
      fi
    else
      echo "[install-prereqs] apt-get not available; cannot install curl/wget"
    fi
  fi

  if command -v curl >/dev/null 2>&1 || command -v wget >/dev/null 2>&1; then
    if [ "$(id -u)" -ne 0 ] && command -v sudo >/dev/null 2>&1; then
      echo "[install-prereqs] running installer with sudo"
      if command -v curl >/dev/null 2>&1; then
        curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash || echo "[install-prereqs] InstallAzureCLIDeb failed"
      else
        wget -qO- https://aka.ms/InstallAzureCLIDeb | sudo bash || echo "[install-prereqs] InstallAzureCLIDeb failed"
      fi
    else
      echo "[install-prereqs] running installer as root or without sudo"
      if command -v curl >/dev/null 2>&1; then
        curl -sL https://aka.ms/InstallAzureCLIDeb | bash || echo "[install-prereqs] InstallAzureCLIDeb failed"
      else
        wget -qO- https://aka.ms/InstallAzureCLIDeb | bash || echo "[install-prereqs] InstallAzureCLIDeb failed"
      fi
    fi
  else
    echo "[install-prereqs] curl/wget still not available; cannot run InstallAzureCLIDeb script"
  fi
fi

if command -v az >/dev/null 2>&1; then
  echo "[install-prereqs] running az bicep install if needed"
  az bicep install || echo "[install-prereqs] az bicep install failed"
fi

# Ensure common bicep locations are on PATH so pwsh can find the binary
if ! command -v bicep >/dev/null 2>&1; then
  for d in "$HOME/.azure/bin" "$HOME/.local/bin" "$HOME/.dotnet/tools" /usr/local/bin; do
    if [ -x "$d/bicep" ] || [ -f "$d/bicep" ]; then
      export PATH="$d:$PATH"
      echo "[install-prereqs] added $d to PATH"
      break
    fi
  done
fi

echo "[install-prereqs] bicep path: $(command -v bicep 2>/dev/null || echo not-found)"
echo "[install-prereqs] PATH=$PATH"

echo "[install-prereqs] Done"
