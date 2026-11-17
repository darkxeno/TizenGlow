# Contexto de Desarrollo y Operaciones — TizenGlow (`darkxeno/TizenGlow`)

## 1. Herramientas Locales y Rutas en este Mac
- **Binario `.NET SDK` (`dotnet`):** `/Users/tino/.dotnet/dotnet`
- **Binario `sdb` (Tizen Smart Development Bridge):** `/Users/tino/Development/Projects/tizen-tv/samsung-tv-root/sdb-bin/sdb`
- **Repositorio principal:** `/Users/tino/Development/Projects/tizen-tv/tizenglow`
- **Repositorio GitHub:** `darkxeno/TizenGlow`

### Compilar y ejecutar tests localmente
```sh
# Compilar el paquete .tpk en Release (sin restaurar si ya está en caché)
/Users/tino/.dotnet/dotnet build TizenGlow/TizenGlow.csproj -c Release --no-restore

# Ejecutar tests unitarios
/Users/tino/.dotnet/dotnet test TizenGlow.Tests/TizenGlow.Tests.csproj --no-restore --verbosity normal
```
El `.tpk` generado queda en:
`TizenGlow/bin/Release/tizen90/io.gh.darkxeno.TizenGlow-<version>.tpk`

---

## 2. Versionado y Flujo de Release (PR + GitHub Actions)
El workflow `.github/workflows/release.yml` exige que **los 3 archivos siguientes tengan exactamente la misma versión**:
1. `TizenGlow/tizen-manifest.xml` (`version="X.Y.Z"`)
2. `package.json` (`"version": "X.Y.Z"`)
3. `TizenGlowUI/js/wsClient.js` (`const uiVersion = 'X.Y.Z';`)

### Proceso habitual para publicar nueva versión
1. Crear rama desde `origin/main` y actualizar los 3 archivos de versión.
2. Hacer commit, `git push -u origin <rama>` y crear PR con:
   ```sh
   gh pr create --repo darkxeno/TizenGlow --base main --head <rama> --title "..." --body "..."
   ```
3. Al hacer **merge** a `main`, GitHub Actions compila y publica automáticamente la Release `vX.Y.Z` con el `.tpk` adjunto.
4. **Importante sobre certificados e instalación:**
   - Si instalas desde **Apps2Samsung** (certificado `Jelly2Sams - Partner`) y luego instalas un `.tpk` subido por **TV-Store** (o viceversa con distinto certificado), Tizen desinstala y reinstala la app borrando las preferencias (`Preference`).
   - Para conservar la configuración entre actualizaciones, instala siempre con la misma herramienta/certificado.

---

## 3. Red Local, TV y Dispositivos
- **TV:** Samsung `QE65S95BATXXC` (S95B, Tizen `6.5`), IP `192.168.10.38`
  - **Web UI embebida (TizenGlow):** `http://192.168.10.38:8090`
  - **Logs en memoria del servicio:** `http://192.168.10.38:8090/logs`
  - **WebSocket del servicio:** `ws://192.168.10.38:8091`
  - **TV-Store (subida directa de `.tpk`):** `http://192.168.10.38:8077`
    ```sh
    curl -sS -X PUT --data-binary "@TizenGlow/bin/Release/tizen90/io.gh.darkxeno.TizenGlow-<version>.tpk" \
      "http://192.168.10.38:8077/upload?name=io.gh.darkxeno.TizenGlow-<version>.tpk"
    ```
  - **Puerto SDB (`26101`):**
    - Si en **Apps → `12345` (Developer Mode)** la **Host PC IP** está en `127.0.0.1`, funcionan **Apps2Samsung / TV-Store** dentro de la TV, pero rechaza conexiones `sdb` desde el Mac (`192.168.10.198`).
    - Si se pone `192.168.10.198`, conecta `sdb connect 192.168.10.38:26101` desde el Mac.
- **WLED / Puente DDP:** `192.168.10.132:4048` (ESP32 WLED también en `192.168.10.39:4048`)
- **Nanoleaf Aurora:** `192.168.10.168` (API HTTP `16021`, UDP `extControl` `60221`)

### Configuración del Usuario (Referencia para restaurar por WebSocket `:8091`)
- `outputMode`: `"wled"`
- `wledTarget`: `"192.168.10.132:4048"`
- `ledLayout`: `"120,120,20,0,bottomright,ccw"` (`Top: 120`, `Right: 120`, `Bottom: 20`, `Left: 0`, `Bottom Right`, `Counter-clockwise`)
- `ledShift`: `"0"`
- `wledBrightness`: `"30"`
- `wledBlackThreshold`: `"0"`
- `wledSmoothing`: `"on"`
- `wledSaturation`: `"175"`
- `wledGamma`: `"2.0"`
- `wledTvOff`: `"off"`
- `captureMode`: `"detailed"`
- `targetFps`: `"10"`
- `letterbox`: `"auto"`
- `vpEnabled`: `"off"`
- `levels`: `"on"`
- `uiScale`: `"100"`
- Zonas combinadas WLED + Nanoleaf:
  - `wledTopRange`: `Right corner (62.5–100%)`
  - `nanoleafEdge`: `top`, `nanoleafRange`: `Center (8–78%)`

---

## 4. Arquitectura de Captura en S95B (Tizen 6.5)
1. **`libvideoenhance.so` (`cs_ve`) — Motor nativo `.tpk` en S95B:**
   - Funciones: `cs_ve_get_rgb_measure_condition`, `cs_ve_set_rgb_measure_position`, `cs_ve_get_rgb_measure_pixel`.
   - En la S95B devuelve `3840x2160, points=2` (lee 2 puntos por ronda y espera `SleepMS` tras cada ronda).
   - Es el mismo motor que usa la app oficial **Philips Hue Sync TV** (`HueSync.dll`).
   - En modo `Detailed` (14 puntos = 7 rondas) da ~6–7 FPS reales; en `Balanced` (8 puntos = 4 rondas) o `Fast` (6 puntos = 3 rondas) sube proporcionalmente los FPS.
2. **`libvideo-capture.so.0.1.0` (`secvideo_api_capture_video_main` — `VideoPlaneCapturer.cs`):**
   - La librería y el símbolo existen en la S95B (Tizen 6.5), pero la llamada devuelve **`rc = -95` (`-EOPNOTSUPP`)** incluso firmando el `.tpk` con certificado **Partner** (`Jelly2Sams - Partner`).
   - Por eso se añadió la preferencia `vpEnabled` (`VideoPlane: < Off | On >`, desactivada por defecto desde `v1.4.4`) y el estado `videoPlaneStatus` en **Status** y `/logs`.
3. **`libtzcapturec.so` (`TZCAPTURE_Capture_screen` / `TZCAPTURE_Secure_Capture`):**
   - Opera sobre TrustZone (`libteec.so` / `/dev/tzdev*` con permisos `crw------- 1 owner root`) e incluye `checkProcessWhitelist` sobre `/proc/self/cmdline`.
   - Devuelve buffers YUV 4:2:0 semi-planares (`720×576`: `Y = 414720` bytes, `C = 207360` bytes) en ~3.5–6.2 ms por captura.
   - No es invocable directamente desde un `.tpk` normal sin privilegios; para integrarlo con TizenGlow se usa el receptor UDP local (`127.0.0.1:19446`, protocolo binario `TGVP`).
