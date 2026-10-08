# Bromirror

Windows-App mit zwei Richtungen:

- **iPhone → PC** – AirPlay-Bildschirmsynchronisierung vom iPhone auf den PC (UxPlay als Engine).
- **PC → TV** – einen PC-Bildschirm live in den Browser jedes Smart-TVs streamen, ohne App auf dem Fernseher.

Dunkles UI mit Orange-Akzent, HUD-Intro beim Start, Watchdog mit Auto-Neustart.

## Funktionen

### iPhone → PC
- Erscheint auf dem iPhone unter *Kontrollzentrum → Bildschirmsynchronisierung* (Name einstellbar).
- Status in Echtzeit (Bereit / Verbunden mit Gerätename, Modell, IP / Neustart / Fehler).
- **Watchdog:** stürzt UxPlay ab, startet Bromirror es nach 2 s neu; bei 5 Abstürzen in 60 s Fehlerstatus statt Endlosschleife.
- Feste Geräte-Identität, damit das iPhone den PC wiedererkennt.
- Direct3D-11-Rendering, Vollbild per Alt+Enter oder Einstellung.
- Warnung + Beenden-Knopf, wenn PigeonCast parallel läuft.

### PC → TV
- Startet einen kleinen HTTP-Server (Port 80, sonst 47100/8090); der TV-Browser öffnet `http://<PC-IP>`.
- Livebild als MJPEG – läuft in praktisch jedem TV-Browser, ohne Installation.
- Auswahl des Bildschirms, 720p/1080p, 15/24/30 fps, Mauszeiger an/aus – greift sofort, auch im laufenden Stream.
- Anzeige von Zuschauern, Bildrate, Datenrate und eine Vorschau.

**Grenzen:** kein Ton, ca. 0,2–0,5 s Verzögerung (nicht zum Zocken), 1080p braucht ~27 Mbit/s.
DRM-geschützte Inhalte (Netflix & Co.) bleiben schwarz. Solange der Sender läuft, kann jedes Gerät im
Netzwerk den Bildschirm sehen – daher startet er nur auf Knopfdruck.

## Voraussetzungen

- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/) (zum Bauen) bzw. .NET 10 Desktop Runtime (zum Ausführen)
- [MSYS2](https://www.msys2.org/) unter `C:\msys64` (UxPlay und GStreamer laufen aus `C:\msys64\ucrt64`)

## Bauen

```bash
# 1. UxPlay holen, patchen, bauen (MSYS2-Bash oder Git Bash)
bash scripts/build-uxplay.sh

# 2. Bromirror bauen
cd Bromirror
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ../app
```

Danach `app/Bromirror.exe` starten. Beim ersten Start fragt die Windows-Firewall – Zugriff für
**private Netzwerke** erlauben (für AirPlay und für PC → TV).

## Startparameter

| Parameter | Wirkung |
|---|---|
| `--minimized` | minimiert starten, ohne Intro (für den Windows-Autostart) |
| `--tv` | direkt den Tab PC → TV öffnen und den Sender starten |
| `--snapshot <datei.png>` | Selbsttest: Fenster nach 6 s als PNG speichern und beenden |
| `--intro-snapshot <datei.png>` | Selbsttest: Intro rendern und beenden |

## Aufbau

```
Bromirror/            WPF-App (.NET 10)
  MirrorEngine.cs     steuert uxplay.exe: Start/Stopp, Watchdog, Verbindungserkennung
  ScreenStreamer.cs   PC → TV: Bildschirmaufnahme + MJPEG-HTTP-Server
  MainWindow*.xaml    Oberfläche (Tabs iPhone → PC / PC → TV)
  IntroWindow.xaml    HUD-Intro
patches/              Bromirror-Änderung an UxPlay (ungepufferte Ausgabe für Live-Status)
scripts/              Build-Skript für UxPlay
```

Einstellungen liegen in `%APPDATA%\Bromirror\settings.json`, Logs in `logs/`.

## Lizenzen

- **UxPlay** ([FDH2/UxPlay](https://github.com/FDH2/UxPlay)) steht unter GPL-3.0. Es ist nicht in diesem
  Repo enthalten, sondern wird vom Build-Skript geholt und mit `patches/` minimal angepasst. Bromirror
  startet UxPlay als eigenständiges Programm.
- Für Bromirror selbst ist noch keine Lizenz festgelegt.
