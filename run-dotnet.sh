#!/bin/bash
proot-distro login ubuntu --shared-tmp -- bash -c "
  export DOTNET_ROOT=/root/.dotnet
  export PATH=\$DOTNET_ROOT:\$PATH
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
  export DOTNET_NOLOGO=1
  cd /root/AlphaApp
  $*
"
