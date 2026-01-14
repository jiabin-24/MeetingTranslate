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
# Install .NET SDK (dotnet) if missing
if command -v dotnet >/dev/null 2>&1; then
  echo "[install-prereqs] dotnet present: $(command -v dotnet)"
else
  echo "[install-prereqs] dotnet not found — attempting install"
  if command -v apt-get >/dev/null 2>&1; then
    if command -v curl >/dev/null 2>&1 || command -v wget >/dev/null 2>&1; then
      if [ "$(id -u)" -ne 0 ] && command -v sudo >/dev/null 2>&1; then
        SUDO="sudo"
      else
        SUDO=""
      fi

      echo "[install-prereqs] downloading Microsoft package config"
      if command -v curl >/dev/null 2>&1; then
        curl -sSL https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -o /tmp/packages-microsoft-prod.deb || echo "[install-prereqs] failed to download packages-microsoft-prod.deb"
      else
        wget -qO /tmp/packages-microsoft-prod.deb https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb || echo "[install-prereqs] failed to download packages-microsoft-prod.deb"
      fi

      if [ -f /tmp/packages-microsoft-prod.deb ]; then
        $SUDO dpkg -i /tmp/packages-microsoft-prod.deb || echo "[install-prereqs] dpkg install of microsoft packages failed"
        $SUDO apt-get update -y || echo "[install-prereqs] apt-get update failed"
        $SUDO apt-get install -y dotnet-sdk-8.0 || echo "[install-prereqs] apt-get install dotnet-sdk-8.0 failed"
      else
        echo "[install-prereqs] microsoft package file not present; skipping dotnet install"
      fi
    else
      echo "[install-prereqs] curl/wget not available; cannot download Microsoft package config for dotnet"
    fi
  else
    echo "[install-prereqs] apt-get not available; skipping dotnet install"
  fi
fi

# Install Node.js (node & npm) if missing
if command -v node >/dev/null 2>&1 && command -v npm >/dev/null 2>&1; then
  echo "[install-prereqs] node present: $(command -v node), npm: $(command -v npm)"
else
  echo "[install-prereqs] node/npm not found — attempting install"
  if command -v apt-get >/dev/null 2>&1; then
    if command -v curl >/dev/null 2>&1 || command -v wget >/dev/null 2>&1; then
      if [ "$(id -u)" -ne 0 ] && command -v sudo >/dev/null 2>&1; then
        SUDO="sudo"
      else
        SUDO=""
      fi

      echo "[install-prereqs] setting up NodeSource (Node 18)"
      if command -v curl >/dev/null 2>&1; then
        curl -fsSL https://deb.nodesource.com/setup_18.x | $SUDO bash - || echo "[install-prereqs] NodeSource setup failed"
      else
        wget -qO- https://deb.nodesource.com/setup_18.x | $SUDO bash - || echo "[install-prereqs] NodeSource setup failed"
      fi

      $SUDO apt-get install -y nodejs || echo "[install-prereqs] apt-get install nodejs failed"
    else
      echo "[install-prereqs] curl/wget not available; cannot run NodeSource setup"
    fi
  else
    echo "[install-prereqs] apt-get not available; skipping node/npm install"
  fi
fi

echo "[install-prereqs] dotnet path: $(command -v dotnet 2>/dev/null || echo not-found)"
echo "[install-prereqs] node path: $(command -v node 2>/dev/null || echo not-found), npm: $(command -v npm 2>/dev/null || echo not-found)"

echo "[install-prereqs] Done"
