# Kaimo Files – übergreifendes Design-Spec

**Status:** Verbindliche visuelle und konzeptionelle Grundlage  
**Geltungsbereich:** Weboberfläche sowie zukünftige Desktop-, iOS- und Android-Apps  
**Detailgrad:** Abstraktes System-Spec; konkrete Seiten und Workflows werden separat spezifiziert  
**Technische Basis der Weboberfläche:** Blazor und Bootstrap bleiben erhalten

## 1. Kurzfassung für KI-Systeme

Gestalte Kaimo Files als eigenständige, ausdrucksstarke Fileserver-Oberfläche mit hoher Orientierung und sichtbaren Funktionen. Die Bedienphilosophie orientiert sich an guten älteren Windows-Oberflächen: klare Arbeitsbereiche, erkennbare Hierarchien, persistente Navigation, sichtbare Befehle, Tabellen, Baumansichten, Toolbars, Kontextinformationen und Statusanzeigen. Diese Orientierung darf nicht als Retro-Kopie oder Nostalgie-Theme umgesetzt werden. Sie wird mit moderner Typografie, sauberem Spacing, zeitgemäßer Barrierefreiheit und responsivem Verhalten neu interpretiert.

Die Farbwelt ist überwiegend neutral. Grün auf Basis von `#009E60` ist die charakteristische Akzentfarbe, wird aber bewusst selten eingesetzt. Nicht jede Fläche, Auswahl, Überschrift oder Datei darf grün sein. Light- und Dark-Mode besitzen mehrere klar unterscheidbare neutrale Ebenen, damit Navigation, Arbeitsfläche, Panels und Dialoge auch ohne Farbe sofort verständlich bleiben. Bootstrap darf technisch weiterverwendet werden, sein Default-Look darf jedoch nirgends erkennbar sein.

## 2. Verbindlichkeit

Die Schlüsselwörter **MUSS**, **DARF NICHT**, **SOLL** und **KANN** sind normativ:

- **MUSS / DARF NICHT:** zwingende Anforderung.
- **SOLL:** Standardentscheidung; Abweichungen benötigen eine nachvollziehbare funktionale Begründung.
- **KANN:** zulässige Option, aber kein Standard.

Konkrete Seiten-Specs dürfen diese Regeln ergänzen, jedoch nicht stillschweigend aufheben.

## 3. Designidentität

### 3.1 Gewünschte Wirkung

Kaimo Files MUSS wirken wie ein fokussiertes Werkzeug, dem Nutzer ihre Daten und Infrastruktur anvertrauen können. Die Oberfläche soll folgende Eigenschaften vermitteln:

- übersichtlich, direkt und verlässlich;
- funktional reichhaltig, ohne visuell überladen zu sein;
- technisch kompetent, aber nicht spröde;
- ausdrucksstark und wiedererkennbar, aber nicht dekorativ verspielt;
- für Enthusiasten und professionelle Nutzer effizient, zugleich für weniger erfahrene Nutzer lesbar.

Die visuelle Persönlichkeit kann subtil an eine Schildkröte erinnern: ruhig, robust, langlebig und geschützt. Diese Assoziation SOLL über das Grün, stabile Formen und eine ruhige Anmutung entstehen – nicht durch ein allgegenwärtiges Tiermotiv.

### 3.2 Modernisierte Windows-Prinzipien

Die Referenz an ältere Windows-UIs meint insbesondere:

- Funktionen sind dort sichtbar, wo sie gebraucht werden.
- Primäre Befehle besitzen Textlabels oder eindeutig gelernte Symbole.
- Navigation, Inhalt, Eigenschaften und Status sind als unterschiedliche Bereiche erkennbar.
- Tabellen, Tree Views, Split Panes, Toolbars und Statusleisten sind legitime Hauptkomponenten.
- Rechtsklick, Hover und Tastaturkürzel beschleunigen die Bedienung, sind aber nie der einzige Zugang zu einer wichtigen Funktion.
- Eine Auswahl führt zu sichtbaren, kontextbezogenen Befehlen und Informationen.
- Informationsdichte wird geordnet, nicht pauschal reduziert.

Nicht gemeint sind pixelige Retro-Grafiken, starke 3D-Bevels, graue 1990er-Jahre-Dialoge, imitierte Betriebssystem-Chrome oder eine direkte Kopie des Windows Explorers.

## 4. Kernprinzipien

### 4.1 Sichtbarkeit vor Verstecken

- Häufige und wichtige Aktionen MÜSSEN ohne Kontextmenü, Hover-Offenlegung oder verschachteltes Overflow-Menü erreichbar sein.
- Kontextmenüs SOLLEN dieselben Befehle als schnelleren Zweitzugang anbieten.
- Ein Overflow-Menü DARF nur seltene oder klar sekundäre Aktionen aufnehmen.
- Icon-only-Buttons DÜRFEN nur für universell bekannte Befehle oder sehr enge Toolbars verwendet werden und benötigen immer Tooltip und zugänglichen Namen.
- Fortschritt, Auswahl, Filter, Sortierung, Berechtigungsumfang und aktive Synchronisationen MÜSSEN sichtbar kommuniziert werden.

### 4.2 Progressive Offenlegung ohne Funktionsverlust

Die Oberfläche soll nicht alles gleichzeitig zeigen. Sie MUSS jedoch erkennen lassen, dass weitere Optionen existieren. Geeignete Mittel sind Tabs, aufklappbare Sektionen, Eigenschaften-Panels, Split Views und klar bezeichnete „Weitere Details“-Bereiche. Kritische oder häufige Funktionen DÜRFEN dadurch nicht versteckt werden.

### 4.3 Struktur vor Dekoration

Hierarchie entsteht zuerst durch Position, Abstände, Typografie, Trennlinien und unterschiedliche neutrale Flächen. Farbe und Schatten sind nachrangig. Große dekorative Flächen, Hero-Bereiche oder Marketing-Layouts gehören nicht in die angemeldete Anwendung.

### 4.4 Ruhige Informationsdichte

Der Standard ist eine ausgewogene bis kompakte Arbeitsoberfläche. Sie soll mehr Informationen als eine typische Consumer-Cloud-App zeigen, aber ausreichend Zeilenhöhe, Gruppierung und Weißraum für schnelles Scannen besitzen. Große leere Cards, übergroße Überschriften und unnötig hohe Controls sind zu vermeiden.

## 5. Farbsystem

### 5.1 Grundregel

Mindestens 85 Prozent der wahrgenommenen Oberfläche SOLLEN aus neutralen Farben bestehen. Grün ist für Marke, primäre Handlung, aktive Auswahl, Fokus und positive Zustände reserviert. Es DARF NICHT zur universellen Einfärbung aller Icons, Dateitypen, Navigationsflächen, Tabellenzeilen oder Überschriften werden.

Andere Farben sind für echte semantische Zustände erlaubt und erwünscht: Rot für Gefahr/Fehler, Amber für Warnung, Blau für neutrale Information. Sie dürfen nicht als konkurrierende Markenfarben eingesetzt werden.

### 5.2 Marken- und Interaktionsgrün

`#009E60` ist die kanonische Markenfarbe. Für ausreichenden Kontrast werden je Theme funktionale Varianten verwendet.

| Token | Light Mode | Dark Mode | Verwendung |
|---|---:|---:|---|
| `--color-accent-brand` | `#009E60` | `#19B972` | Logo, charakteristische Details, größere grafische Akzente |
| `--color-accent-action` | `#007A49` | `#35C986` | interaktive Akzente und kontrastkritische Elemente |
| `--color-accent-hover` | `#006D41` | `#50D69A` | Hover eines grünen Elements |
| `--color-accent-active` | `#005D38` | `#22AF70` | gedrückter Zustand |
| `--color-accent-soft` | `#DDF4E9` | `#163D2B` | sparsame Auswahl- oder Fokusfläche |
| `--color-accent-border` | `#71C8A0` | `#287D55` | Akzentkante, Auswahlrahmen |

`#009E60` mit weißer Schrift ist nicht automatisch für kleinen Text geeignet. Kontrastkritische Buttons im Light Mode SOLLEN deshalb `--color-accent-action` verwenden. Farbe darf niemals der einzige Informationsträger sein.

### 5.3 Neutrale Flächen

Die Flächen unterscheiden sich bewusst in Helligkeit und teilweise leicht in Temperatur. Damit wird verhindert, dass im Light- oder Dark-Mode „alles gleich aussieht“.

| Rolle | Light Mode | Dark Mode |
|---|---:|---:|
| `--color-canvas` | `#F2F3F1` | `#151816` |
| `--color-surface` | `#FFFFFF` | `#1E221F` |
| `--color-surface-raised` | `#F8F9F7` | `#272C28` |
| `--color-surface-sunken` | `#E9ECE8` | `#111411` |
| `--color-surface-hover` | `#E5E9E5` | `#303630` |
| `--color-border` | `#C8CDC8` | `#3B433C` |
| `--color-border-strong` | `#959D97` | `#59625B` |
| `--color-text` | `#1B201C` | `#F0F3F0` |
| `--color-text-secondary` | `#505852` | `#BCC3BD` |
| `--color-text-muted` | `#707872` | `#929B94` |

Canvas, Sidebar, Inhaltsfläche, Toolbar, Tabellenkopf und Dialog MÜSSEN auch in Graustufen voneinander unterscheidbar bleiben. Trennung darf nicht ausschließlich durch Schatten erfolgen.

### 5.4 Semantische Farben

- Erfolg: Grün, aber in Kombination mit Icon und Text.
- Information: zurückhaltendes Blau ausschließlich für Informationszustände.
- Warnung: Amber/Ocker.
- Fehler und destruktive Aktion: Rot.
- Inaktiv/deaktiviert: neutral, niemals nur reduzierte Deckkraft bis zur Unlesbarkeit.

Semantische Hintergründe SOLLEN getönt und zurückhaltend sein; Vollfarben sind Badges, kleinen Indikatoren und wichtigen Aktionen vorbehalten.

## 6. Typografie

- Die UI MUSS eine gut lesbare, neutrale Sans-Serif-Schrift verwenden. Plus Jakarta Sans KANN für Marke und größere Überschriften erhalten bleiben; für die Arbeitsoberfläche ist eine robuste Systemschrift oder eine ähnlich klare UI-Schrift zulässig.
- Überschriften sind kompakt und sachlich. Seitenüberschriften SOLLEN nicht wie Marketing-Headlines wirken.
- Normale UI-Texte SOLLEN überwiegend zwischen 13 und 15 px liegen; 12 px ist nur für Metadaten zulässig.
- Pfade, Hashes, Ports, technische IDs und Logausgaben KÖNNEN eine Monospace-Schrift verwenden.
- Gewicht und Größe müssen Hierarchie erzeugen. Grün DARF NICHT als Ersatz für typografische Hierarchie dienen.
- Dateinamen und primäre Objektbezeichnungen besitzen höhere Priorität als ergänzende Metadaten.

## 7. Form, Flächen und Tiefe

- Standardradien SOLLEN moderat sein: 4–6 px für Controls, 6–8 px für Panels und Dialoge. Pillenformen sind Badges, Tags und echten binären Statusanzeigen vorbehalten.
- Die Formensprache ist stabil und leicht technisch, nicht hart, aber auch nicht übermäßig weich.
- Trennlinien sind ein bewusstes Gestaltungsmittel. Tabellen, Toolbars und Split Panes dürfen sichtbare, zurückhaltende Grenzen besitzen.
- Schatten werden sparsam verwendet: primär für Dialoge, Popovers und tatsächlich überlagerte Ebenen.
- Dauerhaft nebeneinanderliegende Bereiche SOLLEN durch Flächen und Borders statt schwebender Cards getrennt werden.
- Ein Bildschirm DARF NICHT in eine Sammlung gleich aussehender, frei schwebender Bootstrap-Cards zerfallen.

## 8. Icons und Bildsprache

- Es MUSS ein konsistentes Iconset mit einheitlicher Strichstärke, optischer Größe und Ausrichtung geben.
- Icons sollen konkret und schnell erkennbar sein. Übermäßig abstrakte Symbole sind zu vermeiden.
- Datei- und Ordnertypen dürfen vertraute, zurückhaltende Eigenfarben verwenden. Sie werden nicht pauschal grün eingefärbt.
- Hauptaktionen in Toolbars SOLLEN Icon und Text kombinieren. Sekundäre kompakte Aktionen KÖNNEN Icon-only sein.
- Das Schildkrötenmotiv KANN in Logo, Maskottchen, Onboarding oder ausgewählten Empty States erscheinen. Es DARF NICHT jede Ansicht dekorieren.

## 9. Layout- und Navigationslogik

### 9.1 Desktop und Web

- Die primäre Navigation SOLL dauerhaft sichtbar oder eindeutig einklappbar sein.
- Arbeitsbereiche sollen klare Zonen besitzen: Navigation, Befehle, Inhalt, Details/Eigenschaften und Status.
- Inhaltsseiten SOLLEN eine kompakte Befehlsleiste statt verstreuter Einzelbuttons verwenden.
- Tabellen- und Listenansichten sind Standard für informationsreiche Bereiche.
- Eigenschaften und Berechtigungen SOLLEN bevorzugt in einem Detailpanel, Split Pane oder klaren Dialog erscheinen.
- Suche, Filter und Sortierung müssen ihren aktiven Zustand sichtbar zeigen.
- Auswahlzustände müssen in hellen und dunklen Themes deutlich sein, ohne die gesamte Zeile kräftig grün zu färben.

### 9.2 Responsives Verhalten

Responsivität bedeutet Priorisierung, nicht bloß Verkleinerung:

- Auf großen Displays können Navigation, Liste und Detailpanel gleichzeitig sichtbar sein.
- Auf kleineren Displays werden diese Bereiche schrittweise zu umschaltbaren Ebenen.
- Wichtige Aktionen bleiben direkt erreichbar; sekundäre Aktionen können in ein klar beschriftetes Menü wechseln.
- Horizontale Tabellen dürfen Spalten priorisieren, ausblenden oder in eine strukturierte Listenform wechseln.
- Touch-Ziele müssen größer werden, ohne dass die Desktopansicht unnötig grob wird.

### 9.3 Zukünftige Apps

Web, Desktop, iOS und Android teilen Farbtokens, Typografieprinzipien, Icons, Zustände und Markenidentität. Navigation und Interaktionsmuster SOLLEN jedoch an die jeweilige Plattform angepasst werden. Eine Desktop-Toolbar darf nicht unverändert auf ein Smartphone kopiert werden; ebenso darf eine mobile Bottom Navigation nicht erzwungen auf Desktop erscheinen.

## 10. Komponentencharakter

### 10.1 Buttons

- Pro lokaler Befehlsgruppe SOLL es höchstens eine visuell dominante Primäraktion geben.
- Primärbuttons verwenden das funktionale Grün nur, wenn die Aktion wirklich primär ist.
- Sekundärbuttons sind neutral mit klarer Border oder dezenter Fläche.
- Destruktive Buttons sind rot und dürfen nie grün erscheinen.
- Alle Zustände – Default, Hover, Active, Focus, Disabled und Loading – müssen definiert sein.

### 10.2 Tabellen und Listen

- Tabellenköpfe, Datenzeilen, Auswahl und Hover müssen als unterschiedliche Zustände erkennbar sein.
- Relevante Spalten bleiben sichtbar; weniger wichtige Metadaten dürfen responsiv reduziert werden.
- Sortierung und Spaltenänderungen werden direkt am Header angezeigt.
- Mehrfachauswahl aktiviert eine sichtbare kontextuelle Befehlsleiste.
- Zeilenaktionen dürfen nicht ausschließlich beim Hover auftauchen.

### 10.3 Navigation, Tree Views und Tabs

- Aktive Einträge verwenden eine Kombination aus neutraler Flächenänderung, Akzentkante oder kleinem grünen Indikator sowie stärkerem Text.
- Ein vollständig grüner Navigationsblock ist zu vermeiden.
- Hierarchische Daten wie Ordner und Abteilungen SOLLEN als echte Tree View mit klarer Einrückung und Auf-/Zuklappzuständen erscheinen.
- Tabs müssen wie Navigation innerhalb eines Bereichs wirken und dürfen nicht mit Buttons verwechselt werden.

### 10.4 Dialoge und Panels

- Dialoge besitzen klaren Titel, strukturierten Inhalt und eine vorhersehbare Aktionszone.
- Primär- und Abbrechen-Aktion sind stabil positioniert.
- Lange Einstellungen SOLLEN gegliedert werden, statt in einem riesigen Formular zu enden.
- Häufig konsultierte Eigenschaften gehören in ein persistentes Panel; seltene, fokussierte Aufgaben können Dialoge sein.

### 10.5 Feedback und Systemzustände

Loading, Empty, Error, Offline, Disabled, Permission Denied, Partial Success und Background Activity MÜSSEN als eigene Zustände vorgesehen werden. Toasts dürfen dauerhaft relevante Informationen nicht ersetzen. Längere Uploads, Downloads und Synchronisationen benötigen sichtbaren Fortschritt und einen später wieder auffindbaren Status.

## 11. Light- und Dark-Mode

- Beide Themes sind gleichwertige Produkte, nicht automatische Farbinvertierungen.
- Der Standard KANN der Systemeinstellung folgen.
- Jeder Modus benötigt mehrere neutrale Ebenen mit ausreichendem Kontrast.
- Dark Mode verwendet kein reines Schwarz als dominanten Hintergrund und kein reines Weiß für große Textmengen.
- Light Mode verwendet nicht ausschließlich Weiß; Canvas, Panels, Toolbars und eingebettete Bereiche benötigen unterscheidbare neutrale Flächen.
- Grün kann im Dark Mode heller sein, damit Bedeutung und Kontrast erhalten bleiben.
- Statusfarben, Charts, Dateitypen und Fokusindikatoren müssen für jedes Theme separat geprüft werden.

## 12. Bewegung und Interaktion

- Animationen dienen Orientierung und Rückmeldung, nicht Dekoration.
- Hover- und Pressed-Übergänge SOLLEN kurz und direkt sein.
- Panels dürfen sanft ein- und ausblenden; große federnde oder verspielte Bewegungen sind unpassend.
- `prefers-reduced-motion` MUSS berücksichtigt werden.
- Tastaturfokus MUSS jederzeit deutlich sichtbar sein und darf nicht nur über einen Farbwechsel kommuniziert werden.

## 13. Barrierefreiheit

- Ziel ist mindestens WCAG 2.2 AA.
- Normaler Text benötigt mindestens 4,5:1 Kontrast; große Schrift und wesentliche grafische Bedienelemente mindestens 3:1.
- Alle Hauptfunktionen müssen per Tastatur erreichbar sein.
- Fokusreihenfolge, Screenreader-Namen, Fehlermeldungen und Live-Status müssen semantisch korrekt sein.
- Farbe ist niemals der einzige Träger von Auswahl, Erfolg, Fehler, Warnung oder Berechtigung.
- Touch-Ziele SOLLEN mindestens 44 × 44 CSS-Pixel besitzen; dichte Desktopcontrols dürfen kleiner sein, sofern ausreichender Abstand und Tastaturbedienung gewährleistet sind.
- Das Layout muss längere deutsche und englische Texte ohne Abschneiden zentraler Funktionen verkraften.

## 14. Bootstrap-Regeln

Bootstrap bleibt als technische Grundlage ausdrücklich erlaubt und vorgesehen. Es kann Grid, Breakpoints, Utilities, JavaScript-Verhalten und zugängliche Basisfunktionalität liefern. Das visuelle Design MUSS jedoch durch ein eigenes Kaimo-Theme und eigene Komponentenklassen bestimmt werden.

### 14.1 Erforderliche Anpassung

- Bootstrap-Variablen werden zentral auf Kaimo-Tokens gemappt.
- Typografie, Farben, Radien, Borders, Schatten, Fokus, Abstände und alle Interaktionszustände werden überschrieben.
- Komponenten erhalten semantische Kaimo-Klassen oder Wrapper; rohe Bootstrap-Kompositionen sind kein fertiges UI.
- `.btn-primary`, `.card`, `.navbar`, `.nav-tabs`, `.table`, `.modal`, `.alert`, `.form-control` und ähnliche Klassen DÜRFEN nicht mit weitgehend unverändertem Default-Look ausgeliefert werden.
- Das typische Bootstrap-Blau MUSS vollständig aus Marken-, Fokus-, Link- und Primärzuständen entfernt werden. Blau bleibt nur als semantische Informationsfarbe zulässig.
- Bootstrap-Updates dürfen das sichtbare Design nicht verändern; eigene Tokens und Komponentenstyles bilden die stabile visuelle API.

### 14.2 Zu vermeidender Bootstrap-Look

Ein Entwurf gilt als zu generisch, wenn mehrere dieser Merkmale auftreten:

- Standard-Primärblau oder Bootstrap-nahe Farbpalette;
- Standard-Navbar mit unveränderten Abständen und Dropdowns;
- Seiten aus gleichförmigen Cards mit Standardradius und Standardschatten;
- Standardbuttons ohne eigene Zustände und Hierarchie;
- Formulare, Tabellen, Alerts oder Modals, die unmittelbar als Bootstrap erkennbar sind;
- übermäßiger Einsatz der üblichen Bootstrap-Pills und Badges;
- Layouts, die wie eine Beispielseite aus der Bootstrap-Dokumentation wirken.

## 15. Explizite Anti-Patterns

Kaimo Files DARF NICHT:

- jede interaktive oder ausgewählte Fläche grün färben;
- alle Icons in derselben Akzentfarbe darstellen;
- wichtige Funktionen ausschließlich hinter Drei-Punkte-Menüs, Rechtsklick oder Hover verstecken;
- große Mengen leerer Fläche als Synonym für Einfachheit verwenden;
- ausschließlich aus Cards bestehen;
- Light Mode als „alles weiß“ und Dark Mode als „alles dunkelgrau“ behandeln;
- visuelle Hierarchie nur durch Farbe erzeugen;
- Consumer-Cloud-Ästhetik, Marketing-Dashboard oder Cyberpunk-/Neon-Optik imitieren;
- ältere Windows-Oberflächen pixelgenau kopieren;
- native Plattformkonventionen zugunsten künstlicher Gleichheit ignorieren.

## 16. Entscheidungsreihenfolge für konkrete Entwürfe

Wenn spätere Seiten-Specs oder KI-generierte Entwürfe uneindeutig sind, gilt diese Priorität:

1. Funktion und Zustand verständlich machen.
2. Wichtige Aktionen sichtbar und erreichbar halten.
3. Informationshierarchie durch Layout und neutrale Ebenen schaffen.
4. Barrierefreiheit und Plattformkonventionen erfüllen.
5. Grün gezielt zur Orientierung und Markenbildung einsetzen.
6. Dekoration ergänzen, sofern sie die vorherigen Punkte nicht schwächt.

## 17. Abnahmekriterien

Ein Design entspricht diesem Spec nur, wenn alle folgenden Aussagen mit „Ja“ beantwortet werden können:

- Ist die Oberfläche ohne Anleitung in Navigation, Inhalt, Befehle, Details und Status zerlegbar?
- Sind häufige Hauptfunktionen sichtbar, ohne ein verstecktes Menü entdecken zu müssen?
- Bleibt die Oberfläche trotz sichtbarer Funktionen ruhig und scannbar?
- Sind Light- und Dark-Mode jeweils durch mehrere unterscheidbare neutrale Ebenen strukturiert?
- Wird Grün sparsam und bedeutungsvoll statt flächendeckend eingesetzt?
- Ist `#009E60` beziehungsweise eine kontrastgerechte Variante als unverwechselbarer Akzent erkennbar?
- Bleibt das Interface auch in Graustufen strukturell verständlich?
- Wirken Tabellen, Toolbars, Tree Views, Panels, Dialoge und Formulare wie Teile desselben Systems?
- Sind wichtige Zustände auch ohne Farbe erkennbar?
- Ist kein unveränderter Bootstrap-Standardlook mehr sichtbar?
- Wirkt die UI wie ein modernes, robustes Arbeitswerkzeug und nicht wie ein Retro-Skin?
- Lässt sich das System konsistent auf Web, Desktop und Mobile übertragen?

## 18. Wiederverwendbarer Master-Prompt

> Entwirf die angeforderte Kaimo-Files-Oberfläche gemäß dem übergreifenden Design-Spec. Behandle Kaimo Files als robustes, ausdrucksstarkes Fileserver-Werkzeug. Orientiere die Bedienlogik an den Stärken klassischer Windows-Arbeitsoberflächen: sichtbare Befehle, klare Zonen, Toolbars, Listen/Tabellen, Tree Views, Detailpanels und eindeutige Statusanzeigen. Erzeuge keine Retro-Kopie. Nutze moderne Typografie, saubere Abstände, moderate Radien und zugängliche Interaktionen. Baue die Oberfläche überwiegend aus klar unterscheidbaren neutralen Flächen auf. Verwende Grün auf Basis von `#009E60` sparsam für Marke, primäre Aktion, Fokus und Auswahl; färbe nicht die gesamte UI oder alle Icons grün. Light und Dark Mode müssen jeweils mehrere kontrastreiche Ebenen besitzen. Wichtige Funktionen dürfen nicht ausschließlich in Kontext-, Hover- oder Overflow-Menüs liegen. Bootstrap darf intern verwendet werden, aber keine Komponente darf nach Bootstrap-Default aussehen. Begründe Abweichungen vom Spec und definiere alle relevanten Interaktions- und Systemzustände.

---

Dieses Dokument definiert die gemeinsame Designsprache. Seitenspezifische Informationsarchitektur, genaue Inhalte, Komponentenbelegung und Workflows werden in nachgelagerten Specs festgelegt.
