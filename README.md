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
2. **Datumsabgleich direkt starten** wählen oder optional **Gesamten Bestand als Vorschau prüfen**. Eine Vorschau ist keine Voraussetzung mehr. Sie zeigt bisheriges Hinzufügedatum, Veröffentlichungsdatum und Ausnahmen und speichert den Bericht in ShelfCheck.
3. Nach einer abgeschlossenen Vorschau heißt der Startknopf **Vorschauänderungen übernehmen**. Dieser Lauf verwendet ausschließlich deren Änderungskandidaten und offene Fehler, statt den gesamten Bestand erneut zu laden. Die Werte und Bibliotheksfreigaben werden vor dem Schreiben erneut geprüft. Neue Einträge seit der Vorschau werden beim nächsten inkrementellen Lauf erfasst.
4. Nach dem ersten abgeschlossenen Abgleich **Neue und geänderte Einträge abgleichen** verwenden. ShelfCheck fragt seit dem letzten Prüfzeitpunkt gespeicherte Einträge und offene Probleme ab. Eine paketweise Schnellprüfung vergleicht gespeicherte Datumswerte und Pfade: bereits erfolgreich bearbeitete, unveränderte Einträge einschließlich ShelfChecks eigener Schreibvorgänge gelangen nicht erneut in die Arbeitsliste. Auch reine Änderungen anderer Metadaten lösen keinen erneuten Abgleich identischer Datumswerte aus.
5. **Offene Einträge erneut prüfen** bearbeitet gezielt bisher fehlgeschlagene Einträge, Datumskonflikte, unterbrochene Einträge und Einträge ohne Veröffentlichungsdatum. Der Wiederholungsversuch verschiebt den Prüfzeitpunkt für neue Einträge nicht.

Der Abgleich verarbeitet bis zu 128 Einträge pro Paket mit höchstens zehn Schreibversuchen pro Sekunde. Die feste Wartezeit zwischen Lesepaketen ist deutlich verkürzt; die Schreibbegrenzung bleibt erhalten. Bei einem Jellyfin-Bibliotheksscan wartet er. Der Lauf übernimmt das vorhandene Veröffentlichungsdatum für Filme und Serien sowie das Erstausstrahlungsdatum der jeweiligen Episode. Fehlt das Datum, bleibt der Eintrag wie er ist. Ist derselbe Kalendertag schon eingetragen, bleibt auch die vorhandene Uhrzeit erhalten. Abgebrochene oder unterbrochene Läufe lassen sich aus dem gespeicherten Stand fortsetzen. Unter **Laufverlauf** kannst du Ergebnisse filtern und den Bericht mit den sichtbaren Details als Klartext kopieren. **Änderungen dieses Laufs rückgängig machen** stellt den alten Wert wieder her, sofern der Eintrag weiterhin freigegeben ist und noch den von ShelfCheck geschriebenen Wert hat.

Die Datumsdatenbank liegt unter `shelfcheck/dates.db`. Bei aktiviertem Jellyfin-NFO-Speicher kann Jellyfin die Datumsänderung auch in die NFO schreiben. Ein späterer Metadaten-Refresh kann das Datum aus einer vorhandenen NFO oder aus der Dateierstellungszeit übernehmen. Prüfe nach der Vorschau zunächst eine kleine Bibliothek inklusive der Anzeige **Zuletzt hinzugefügt**, deren Sortierung von der Datumsänderung betroffen sein kann.

Der Datumsbericht enthält auch für übersprungene Einträge den verfügbaren Namen, Typ, Dateipfad, passende Bibliotheken und die Jellyfin-ID. Er unterscheidet fehlende Einträge, virtuelle Einträge, fehlende Pfade, nicht lokale Pfade und Pfadausschlüsse. Bei überlappenden Bibliotheken nennt er die abgewählte Bibliothek und deren ausschließenden Pfad. Diese Angaben stehen auch unter **Bericht kopieren**.

Bei älteren Berichten mit der früheren Sammelmeldung werden die sichtbaren Einträge beim Öffnen anhand der aktuellen Jellyfin-Daten ergänzt. Die Ergänzung ist als **Aktuelle Prüfung zu einem älteren Bericht** gekennzeichnet. Ein inzwischen entfernter Eintrag lässt sich weiterhin über seine gespeicherte Jellyfin-ID zuordnen.

## Bericht lesen

| Befund | Bedeutung |
| --- | --- |
| Ohne Video | Im Titel- oder Staffelordner und seinen vollständig geprüften Unterordnern wurde keine von Jellyfin erkannte Videodatei gefunden. |
| Nur Trailer und Extras | Im vollständig geprüften Ordner liegen Videodateien, die Jellyfins Namensregeln als Trailer oder Extras erkennen. |
| NFO fehlt | Bei einer Videodatei fehlt die passende NFO oder bei einer Serie mit Videos die `tvshow.nfo`. Die Prüfung betrifft ausschließlich das Vorhandensein, nicht den Inhalt. |
| Poster fehlt | Ein passendes lokales Bild fehlt. Zentral in Jellyfin gespeicherte Bilder liegen außerhalb dieser Prüfung. |
| Platzhaltercover | Ein ShelfCheck-Platzhalter wurde lokal erkannt oder bei der letzten ShelfCheck-Bildaktion erfasst. Eine erneute Bildaktion sucht wieder nach einem echten Poster. |
| Staffelordner fehlt | Die Serie enthält keinen erkannten Staffelordner. Eine flache Episodenablage kann beabsichtigt sein. |
| Ungeprüft | Ein Bereich war nicht erreichbar, verknüpft, mit `.ignore` markiert oder zu tief verschachtelt. Der betroffene Titel erhält bei unklarem Inhalt keinen Befund „Ohne Video“. |
| Ablage prüfen | Im Stamm einer Serienbibliothek wurden lose Videodateien gefunden. |

Die Übersicht zeigt laufend Titel, gelesene Ordner, Videos und Befunde sowie aktuellen Pfad, Laufzeit und letzte Aktivität. Anklickbare Befundzahlen filtern die Ergebnisse direkt; ihre Zähler berücksichtigen die Bibliotheksauswahl und Pfadsuche. Ein Pfad kann mehrere Befunde haben. Bibliothek, Befundtyp und Pfadsuche lassen sich kombinieren. Pro Seite werden bis zu 50 Treffer angezeigt. Die Bibliotheksauswahl ist einklappbar und zeigt die Anzahl ausgewählter Bibliotheken.

Unter **Dateibestand → Bibliotheken für diesen Scan** kannst du Bibliotheken ausschließen und die Auswahl speichern. Auch enthaltene Unterpfade ausgeschlossener Bibliotheken werden übersprungen. Die Auswahl gilt für den nächsten manuellen oder geplanten Dateiscan und ist unabhängig von der Freigabe für Datumsänderungen.

**Pause** hält den Dateiscan an der aktuellen Stelle im laufenden Server an. **Fortsetzen** arbeitet dort weiter. **Abbrechen** beendet den Scan und speichert die bis dahin erfassten Ergebnisse als Teilbericht. Etwa alle 30 Sekunden wird zusätzlich ein Zwischenstand gespeichert. Nach einem Serverneustart erscheint ein unterbrochener Dateiscan als Teilbericht und kann neu gestartet werden. Ein Dateisystemzugriff, der gerade auf das Netzlaufwerk wartet, muss zurückkehren, bevor Pause oder Abbruch greifen können.

Der Datumsabgleich zeigt zusätzlich Kandidaten, bearbeitete Einträge, Änderungen, Hinweise und Fehler sowie aktuellen Titel, Laufzeit und letzte Aktivität. Die Schnellprüfung nennt die Zahl bereits geprüfter und unverändert ausgelassener Einträge. Einzelne gespeicherte Datumsänderungen erscheinen laufend im Bericht. **Abbrechen** beendet einen aktiven Abgleich mit erhaltenem Änderungsprotokoll. **Pause** speichert den Fortschritt zum späteren Fortsetzen.

## Bulk-Aktionen für Befunde

Über die Kontrollkästchen im Bericht kannst du einzelne Treffer oder die sichtbare Seite auswählen. **Alle passenden Treffer des aktuellen Filters** bezieht weitere Ergebnisseiten ein. Die Vorschau zeigt vor dem Start alle betroffenen Pfade.

- **Defekte Verknüpfungen löschen** bearbeitet ausschließlich Befunde „Ziel der Dateiverknüpfung nicht erreichbar“. Vor jedem Löschen prüft ShelfCheck erneut den Bibliothekspfad, die Dateiverknüpfung und ihr Ziel. Erreichbare Ziele, normale Dateien und Verzeichnisverknüpfungen werden übersprungen. Die Dateiverknüpfung wird endgültig entfernt. Ein zugeordneter Jellyfin-Videoeintrag wird über dessen Bibliotheksverwaltung abgeglichen. Scheitert dieser zweite Schritt, nennt der Bericht den erforderlichen Bibliotheksscan.
- **Ordner ohne Video löschen** bearbeitet Befunde „Ohne Video“ bei Filmen, Serien, Staffeln und noch nicht zugeordneten Titeln. Die Einordnung als „Titel“ verhindert die Auswahl und Löschung nicht mehr. Vor jedem Entfernen prüft ShelfCheck erneut den gesamten Ordner einschließlich aller Unterordner. Videos, Medienplatzhalter, weitere Verknüpfungen, .ignore oder nicht vollständig lesbare Bereiche verhindern die Löschung. Die bestätigte Löschung entfernt den gewählten Ordner, seine videolosen Unterordner und Begleitdateien wie NFOs, Poster und Untertitel. Bibliotheksstämme sowie Ordner, in denen weitere Bibliotheken eingebunden sind, bleiben geschützt. Bei einer einzeln gewählten Staffel bleibt der übergeordnete Serienordner erhalten.
- **Fehlende Bilder holen** bearbeitet Treffer „Poster fehlt“ und „Platzhaltercover“. ShelfCheck ruft für den zugehörigen Jellyfin-Titel die Bildaktualisierung mit den aktivierten Anbietern, deren Reihenfolge und den Bildlimits der Bibliothek auf. Bereits vorhandene echte Bilder werden beibehalten. Ein erfolgreicher Abruf kann die Bilder im Jellyfin-Metadatenverzeichnis speichern. Ob sie direkt im Medienordner landen, bestimmt die Bibliothekskonfiguration. Deshalb kann der Dateiscan weiterhin ein fehlendes lokales Poster melden, obwohl Jellyfin bereits ein echtes Poster anzeigt.

Wenn danach weiterhin kein echtes Poster vorhanden ist, prüft ShelfCheck die aktiven Posteranbieter in ihrer konfigurierten Reihenfolge ausdrücklich auf ein passendes Bild. Erst nach abgeschlossener, fehlerfreier Suche ohne Treffer erhalten **Filme und Serien** das generische Cover. **Staffeln** erhalten ausschließlich das separate Bild **„Kein Staffelposter vorhanden.“**. Episoden erhalten keinen solchen Platzhalter. Bei Anbieter- oder Netzwerkfehlern wird ein Fehler gemeldet und kein Platzhalter neu eingesetzt. Sind Poster deaktiviert oder ist keine geeignete Bildquelle aktiv, bleibt der Eintrag mit einem Hinweis unverändert.

Die Bulk-Anzeige zählt **Platzhalter** getrennt von erfolgreich geholten echten Bildern. Über den Filter **Platzhaltercover** kannst du später erneut nach echten Postern suchen. ShelfCheck erkennt seine Platzhalter am Bildinhalt und ersetzt sie, sobald ein echtes Poster verfügbar ist. Der zusätzliche Index unter `shelfcheck/posters.db` macht auch zentral gespeicherte Platzhalter im Bericht filterbar und bleibt nach einem Serverneustart erhalten. Das passiert ausschließlich innerhalb der gestarteten Bildaktion.

Die Option **Beim Löschen defekter Verknüpfungen anschließend videolose Filmordner samt Begleitdateien entfernen** ist standardmäßig aktiviert. Die Vorschau listet die Verknüpfungen und die anschließend zu prüfenden Filmordner auf. Nach dem Entfernen eines Links wird dessen Filmordner erneut geprüft und nur bei vollständig geprüftem, videolosem Inhalt bereinigt. Solange weitere Videos oder Verknüpfungen darin liegen, bleibt der Filmordner erhalten. Für videolose Staffeln, Serien und andere Titel startest du die bestätigte Aktion **Ordner ohne Video löschen**.

Bulk-Aktionen arbeiten nacheinander und warten auf einen laufenden Jellyfin-Bibliotheksscan. **Bulk-Aktion abbrechen** beendet weitere Arbeit. Fertige Aktionen bleiben erhalten. Fortschritt und die letzten 50 Einzelergebnisse erscheinen im Dashboard. Das vollständige Einzelergebnisprotokoll liegt unter `shelfcheck/actions/<Lauf-ID>.jsonl`. Nach einem Serverneustart wird ein offener Lauf als unterbrochen angezeigt.

Die Bibliotheksauswahl ist nach Jellyfin-Typ gruppiert. Jede Gruppe bietet **Alle auswählen** und **Alle abwählen**. Der Dateiscan unterstützt zusätzlich Musikvideo- und Heimvideobibliotheken. Musik, Bücher und Sammlungen werden mit ihrem Typ angezeigt und sind für den Video-Dateiscan deaktiviert. Der Datumsabgleich umfasst Filme, Serien und gemischte Bibliotheken.

### Welche Ablage wird erkannt?

- Jeder unmittelbare Unterordner eines Bibliothekspfads gilt als Titel. Zusätzliche Gruppierungsebenen wie `Filme/A–Z/A/Filmtitel` benötigen passend konfigurierte Bibliothekspfade. Lose Filme im Bibliotheksstamm werden einzeln geprüft.
- Videodateien werden rekursiv anhand der Endungen des laufenden Jellyfin-Servers erkannt, einschließlich `.strm` und Disc-Dateien wie `.iso` und `.m2ts`. Extras zählen zur Anzahl der Videodateien. Eigene Namensregeln von ShelfCheck sind dafür nicht nötig, da es die Kriterien des Servers verwendet. Geprüft wird die Existenz, die Abspielbarkeit bleibt offen.
- Als Titelposter zählen unter anderem `cover.jpg`, `poster.jpg`, `folder.jpg`, `default.jpg`, bei Filmen auch `movie.jpg` und bei Serien `show.jpg`. Unterstützt werden JPG, JPEG, PNG, WebP und TBN. Bei Filmen zählt auch `Filmtitel-poster.jpg` oder `Filmtitel.jpg` passend zum Videodateinamen. Bei einzelnen Dateien direkt im Stamm ist ein dateibezogenes Poster erforderlich.
- Bei Filmen zählt `movie.nfo` oder eine NFO mit dem Basisnamen der Videodatei; eine `movie.nfo` im Filmordner gilt auch für dort untergeordnet abgelegte Filmversionen. Bei Serien wird `tvshow.nfo` und je Episode eine NFO mit demselben Basisnamen gesucht. Die Prüfung erfolgt beim vorhandenen Verzeichnislesen und öffnet keine NFO-Inhalte. Auch leere NFO-Dateien zählen als vorhanden. Trailer und Extras sowie Musikvideo- und Heimvideobibliotheken erhalten keine NFO-Befunde. Eine fehlende NFO ist ein Hinweis, kein Nachweis für einen Jellyfin-Einlesefehler.
- Staffelordner werden z. B. als `Season 01`, `Staffel 1`, `Series 1`, `S01` und `Specials` erkannt. Das Staffelposter darf auch im Serienordner als `season01-poster.jpg` bzw. `season-specials-poster.jpg` liegen. In gemischten Bibliotheken dienen erkennbare Staffelordner zur Zuordnung als Serie.
- Verzeichnisverknüpfungen werden als ungeprüft gemeldet. Erreichbare Dateiverknüpfungen zählen. Für die Medienablage genügen schreibgeschützte Mounts.

## Entwicklung und Releases

```sh
dotnet build ShelfCheck/ShelfCheck.csproj -c Release
dotnet run --project checks/ScannerChecks.csproj -c Release
```

Ein Tag im Format `v1.2.3.0` startet den GitHub-Workflow. Nach erfolgreichem Build und Scannertests veröffentlicht er das installierbare ZIP und ergänzt den Katalog mit Ziel-ABI und MD5-Prüfsumme. Der Plugin-Bezeichner bleibt über Releases hinweg stabil.
