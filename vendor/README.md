# Vendored Dependencies

## PluginApi.dll

Logi Actions SDK runtime from Logi Plugin Service (v6.3.0.2406).
Distributed by Logitech as part of the free Logi Plugin Service
installer. Vendored here so the plugin can be built in CI without
requiring a macOS machine with LPS installed.

- **Source**: `/Applications/Utilities/LogiPluginService.app/Contents/MonoBundle/PluginApi.dll`
- **Owner**: Logitech / Loupedeck
- **SDK repo**: https://github.com/Loupedeck/PluginSdk
- **No explicit license** published by Logitech for this DLL as
  of April 2026. If Logitech requests removal, this file will be
  deleted and CI will revert to local-only builds.
