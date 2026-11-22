# ShelfCheck für Jellyfin

ShelfCheck prüft Film-, Serien- und gemischte Bibliotheken direkt über die im Server hinterlegten Dateisystempfade. Auch Ordner ohne Jellyfin-Eintrag erscheinen im Bericht. Der Dateibestandsscan liest ausschließlich die Medienablage. Seinen letzten vollständigen Bericht speichert das Plugin im Jellyfin-Datenverzeichnis unter `shelfcheck/report.json`. Der Datumsabgleich ist eine eigene Funktion im Admin-Dashboard.

## Installation

Gebaut für Jellyfin Server **12.4** und .NET 10.

1. Im Admin-Dashboard **Plugins → Repositorys** öffnen und ein Repository namens **ShelfCheck** hinzufügen.
2. Diese Katalog-URL eintragen:

   ```text
   https://raw.githubusercontent.com/brudertomate/jellyfin-plugin-shelfcheck/main/manifest.json
   ```

3. Im Plugin-Katalog **ShelfCheck** installieren und Jellyfin neu starten.
4. Die Plugin-Seite **ShelfCheck** öffnen. Unter **Dateibestand** kannst du den Dateiscan starten. Unter **Datumsabgleich** kannst du Film- und Serienbibliotheken für die Datumsänderung freigeben. Jellyfin bietet für beide Funktionen optionale Zeitpläne unter **Geplante Aufgaben → ShelfCheck**. Die Standardlisten der Zeitpläne sind leer.

Jellyfin bezieht kompatible Plugin-Updates über dieselbe URL. Unter Docker braucht der Jellyfin-Prozess Lesezugriff auf die eingebundenen Medienpfade.

## Datumsabgleich

1. Unter **ShelfCheck → Datumsabgleich** Bibliotheken auswählen und speichern. Standardmäßig ist keine Bibliothek freigegeben. Wenn ein Pfad sowohl in einer freigegebenen als auch in einer anderen Bibliothek liegt, wird der betroffene Eintrag übersprungen.
2. **Vorschau für gesamten Bestand** starten. Die Vorschau zeigt bisheriges Hinzufügedatum, Veröffentlichungsdatum und Ausnahmen. Sie speichert den Vorschau-Bericht in ShelfCheck.
3. **Datumsabgleich starten** wählen. Der Lauf übernimmt das vorhandene Veröffentlichungsdatum für Filme und Serien sowie das Erstausstrahlungsdatum der jeweiligen Episode. Fehlt das Datum, bleibt der Eintrag wie er ist. Ist derselbe Kalendertag schon eingetragen, bleibt auch die vorhandene Uhrzeit erhalten.
4. Nach dem ersten abgeschlossenen Lauf **Neue und geänderte Einträge abgleichen** verwenden. ShelfCheck fragt seit Laufbeginn gespeicherte Einträge und offene Fehler ab. Einträge mit bereits geprüftem unverändertem Metadatenstand erzeugen keine erneuten Schreibvorgänge.

Der Abgleich verarbeitet bis zu 32 Einträge pro Paket mit höchstens zehn Schreibversuchen pro Sekunde. Bei einem Jellyfin-Bibliotheksscan wartet er. Abgebrochene oder unterbrochene Läufe lassen sich aus dem gespeicherten Stand fortsetzen. Unter **Laufverlauf** kannst du Ergebnisse filtern und den Bericht mit den sichtbaren Details als Klartext kopieren. **Änderungen dieses Laufs rückgängig machen** stellt den alten Wert wieder her, sofern der Eintrag weiterhin freigegeben ist und noch den von ShelfCheck geschriebenen Wert hat.

Die Datumsdatenbank liegt unter `shelfcheck/dates.db`. Bei aktiviertem Jellyfin-NFO-Speicher kann Jellyfin die Datumsänderung auch in die NFO schreiben. Ein späterer Metadaten-Refresh kann das Datum aus einer vorhandenen NFO oder aus der Dateierstellungszeit übernehmen. Prüfe nach der Vorschau zunächst eine kleine Bibliothek inklusive der Anzeige **Zuletzt hinzugefügt**, deren Sortierung von der Datumsänderung betroffen sein kann.

## Bericht lesen

| Befund | Bedeutung |
| --- | --- |
| Ohne Video | Im Titel- oder Staffelordner und seinen vollständig geprüften Unterordnern wurde keine von Jellyfin erkannte Videodatei gefunden. |
| Nur Trailer und Extras | Im vollständig geprüften Ordner liegen Videodateien, die Jellyfins Namensregeln als Trailer oder Extras erkennen. |
| Poster fehlt | Ein passendes lokales Bild fehlt. Zentral in Jellyfin gespeicherte Bilder liegen außerhalb dieser Prüfung. |
| Staffelordner fehlt | Die Serie enthält keinen erkannten Staffelordner. Eine flache Episodenablage kann beabsichtigt sein. |
| Ungeprüft | Ein Bereich war nicht erreichbar, verknüpft, mit `.ignore` markiert oder zu tief verschachtelt. Der betroffene Titel erhält bei unklarem Inhalt keinen Befund „Ohne Video“. |
| Ablage prüfen | Im Stamm einer Serienbibliothek wurden lose Videodateien gefunden. |

Die Übersicht zeigt Titel, Staffeln, gelesene Ordner und Videos. Ein Pfad kann mehrere Befunde haben. Bibliothek, Befundtyp und Pfadsuche lassen sich kombinieren. Pro Seite werden bis zu 50 Treffer angezeigt. Ein abgebrochener Scan lässt den vorherigen Bericht verfügbar.

### Welche Ablage wird erkannt?

- Jeder unmittelbare Unterordner eines Bibliothekspfads gilt als Titel. Zusätzliche Gruppierungsebenen wie `Filme/A–Z/A/Filmtitel` benötigen passend konfigurierte Bibliothekspfade. Lose Filme im Bibliotheksstamm werden einzeln geprüft.
- Videodateien werden rekursiv anhand der Endungen des laufenden Jellyfin-Servers erkannt, einschließlich `.strm` und Disc-Dateien wie `.iso` und `.m2ts`. Extras zählen zur Anzahl der Videodateien. Eigene Namensregeln von ShelfCheck sind dafür nicht nötig, da es die Kriterien des Servers verwendet. Geprüft wird die Existenz, die Abspielbarkeit bleibt offen.
- Als Titelposter zählen unter anderem `cover.jpg`, `poster.jpg`, `folder.jpg`, `default.jpg`, bei Filmen auch `movie.jpg` und bei Serien `show.jpg`. Unterstützt werden JPG, JPEG, PNG, WebP und TBN. Bei Filmen zählt auch `Filmtitel-poster.jpg` oder `Filmtitel.jpg` passend zum Videodateinamen. Bei einzelnen Dateien direkt im Stamm ist ein dateibezogenes Poster erforderlich.
- Staffelordner werden z. B. als `Season 01`, `Staffel 1`, `Series 1`, `S01` und `Specials` erkannt. Das Staffelposter darf auch im Serienordner als `season01-poster.jpg` bzw. `season-specials-poster.jpg` liegen. In gemischten Bibliotheken dienen erkennbare Staffelordner zur Zuordnung als Serie.
- Verzeichnisverknüpfungen werden als ungeprüft gemeldet. Erreichbare Dateiverknüpfungen zählen. Für die Medienablage genügen schreibgeschützte Mounts.

## Entwicklung und Releases

```sh
dotnet build ShelfCheck/ShelfCheck.csproj -c Release
dotnet run --project checks/ScannerChecks.csproj -c Release
```

Ein Tag im Format `v1.2.3.0` startet den GitHub-Workflow. Nach erfolgreichem Build und Scannertests veröffentlicht er das installierbare ZIP und ergänzt den Katalog mit Ziel-ABI und MD5-Prüfsumme. Der Plugin-Bezeichner bleibt über Releases hinweg stabil.

Als nächste Ausbaustufen sind eine einzeln bestätigte Bereinigung geprüfter Ordner und das gezielte Nachladen fehlender Bilder vorgesehen.
