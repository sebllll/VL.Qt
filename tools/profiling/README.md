# Profiling (Ultra) – Testpatches und Ablauf

Baseline der CPU-Zeit pro Frame (C#-Nodes, P/Invoke, `vlqt.dll`, Qt Scenegraph/RHI, Render-Thread).
Beschreibung, Auswertung und Vergleich der Läufe: [docs/contributing/profiling.md](../../docs/contributing/profiling.md).

## Szenarien (Vertrag)

Je Szenario ein eigener Patch in diesem Ordner. Gemeinsam für alle:

- Ein `QtRenderer`-Fenster mit fester Größe **1280×720 DIP** (Bounds bzw. `WindowSettings`), kein Save Bounds, kein Fullscreen.
- Keine Perf-/Debug-Overlays, kein Tooltip-/HDE-Kram, keine IOBoxen, die im Export Arbeit machen.
- Animation zeitbasiert (LFO/Time), damit jedes Frame Änderungen hat; die Animation ist deterministisch (kein Random ohne festen Seed).
- Läuft endlos, bis der Prozess beendet wird.
- Assets nur aus `help/Qt/assets/` (kopiert `make-assets.ps1` nach `assets/`, nicht eingecheckt) bzw. von `make-assets.ps1` erzeugt unter `assets/gen/`; das exportierte Programm muss sie finden.

| Datei | Szenario | Inhalt |
|---|---|---|
| `Profile01_Idle.vl` | Leerlauf | leerer `QtRenderer` (Grundlast) |
| `Profile02_Elements.vl` | Viele Elemente | Spread animierter `Rectangle` (Position, Farbe, Rotation) in einer `Group`; Anzahl aus Umgebungsvariable `VLQT_PROFILE_COUNT`, Default 500 (Läufe: 500 und 2000) |
| `Profile03_Typo.vl` | Typo | ~200 `Text`-Elemente, Inhalt wechselt jedes Frame (z. B. Zähler/formatierte Zahl) |
| `Profile04_Images.vl` | Bilder | `LoadImage`/Decode-Worker unter Last: zyklisch wechselnde Bilder (mehrere Dateien, mehrere Image-Elemente), damit dauernd dekodiert wird |
| `Profile05_Video.vl` | Video | Qt-Player (`Video`) mit `help/Qt/assets/qt-help-video.mp4` in Schleife |
| `Profile06_Effects.vl` | Effects | ~20 animierte Elemente mit MultiEffect (Blur + DropShadow) |

Szenario-Namen für `tools/profile.ps1`: `Idle`, `Elements500`, `Elements2000`, `Typo`, `Images`, `Video`, `Effects`
(`Elements500`/`Elements2000` = `Profile02_Elements` mit `VLQT_PROFILE_COUNT=500`/`2000`).

## Ablauf (`tools/profile.ps1`)

```powershell
tools/profile.ps1 [-Scenario <Name[]>|all] [-Out <dir>] [-WarmUp 10] [-Duration 30] [-NoBuild]
```

1. Baut `dotnet build src/VL.Qt -c Release` (außer `-NoBuild`), erzeugt die Testbilder (`tools/profiling/make-assets.ps1`) und exportiert die benötigten Patches per `tools/vvvvc.ps1` als Fenster-Programm (`--output-type WinExe`, dort hält `Renderer [Qt]` die App am Leben) nach `build/profiling/<Patch>/`.
2. Startet sich **einmal** erhöht (ein UAC-Prompt für alle Szenarien) und nimmt je Szenario nacheinander auf:
   startet die Exe selbst (normales Fenster), wartet `<WarmUp>` s, dann
   `dnx -- ultra profile --pid <pid> --duration <Duration> --output <Out>/<rid>_<Szenario> --result-file <Out>/<Szenario>.result.json`;
   danach wird das Programm beendet. Nicht `ultra profile -- <exe>`: so gestartet wird das Fenster nie exposed, und Qt rendert nicht (`basic`-Renderloop), das Profil enthält dann kein `renderWindow`/`Present`.
3. Je Profil `dnx -- ultra report` nach `<Out>/<Szenario>.report.txt`.
4. Schreibt `<Out>/manifest.json`: Git-Commit, RID, CPU, vvvv-Version, Qt-Version, Bildwiederholrate, Szenarien mit Parametern, Pfade.

Default für `-Out`: `%LOCALAPPDATA%\VL.Qt\profiling\<yyMMdd_HHmm>_<rid>\` (außerhalb des Repos, Profile sind mehrere 10 MB).
Symbole: `_NT_SYMBOL_PATH` enthält das Build-Verzeichnis von `vlqt.pdb` und, falls vorhanden, die Qt-PDBs.
Ansicht der Profile: https://profiler.firefox.com/.
