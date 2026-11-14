# Audit NxToWin

## Revisione del 14 novembre 2026

| Area | Intervento | Verifica |
| --- | --- | --- |
| Configurazione | Profili, mappatura, scorciatoie e guadagno nella stessa scheda. Selezione del modello per preparare profili offline. | Salvataggio e ripristino al riavvio di un profilo temporaneo. Verifica nella UI dei comandi disponibili sul Joy-Con 2 sinistro. |
| Layout | Finestra 1260 × 885 DIP, limitata all’area di lavoro del monitor con DPI per-monitor. Barre di scorrimento WPF a tema nei contenitori, menu e registro. | Schermate catturate dalla build Release. Scheda principale e configurazione a piena altezza senza scrollbar di pagina. Barra WPF verificata nel menu delle azioni e trascinata nella finestra ridotta. |
| Scorciatoie | Il controller selezionato viene elaborato prima dell’uscita virtuale. Le azioni Windows e il ricentraggio funzionano anche mentre USB/IP attende il gioco. DSU riceve i pulsanti rimappati e rispetta la sospensione del giroscopio. | Test di Chat C su pressione breve, presenza per modello e pacchetti DSU. |
| Vibrazione | Frequenza Switch 2 logaritmica a 10 bit, conteggio campioni per motore, inoltro dei tre campioni. ViGEm conserva le bande dei due motori. Scritture serializzate, stop su cambio uscita o controller e al termine del test. | Pacchetti con uno e tre campioni, instradamento Joy-Con 2, conversione di frequenza e limite di ampiezza combinata nella suite. |
| Effetti | Campioni da 5 ms raggruppati in pacchetti da 15 ms per Switch 2. Onda stereo, pioggia, impulso GameCube e motivi di Ocarina con pausa fra le note. | Ritmo, intonazione, attacco e rilascio verificati per i tre motivi. |
| Dipendenze e distribuzione | HIDAPI x64 dalla release 0.15.0, avvisi BSD e MIT nel pacchetto. Build Windows e suite in GitHub Actions. | Build Release con zero warning. Scansione NuGet di dipendenze dirette e transitive priva di vulnerabilità segnalate dalle origini configurate. |
| Conflitti Bluetooth | Rilevamento di Switch2Connect anche con suffisso di versione. Il registro del backend VHF registra le transizioni dello streaming, riducendo le righe ripetute. | Il client `Switch2Connect_v2.10` compare tra i possibili conflitti nella build Release. |

SDL usa un campione con intestazione `0x50`. Le acquisizioni e le misure di nxnative documentano blocchi da uno a tre campioni e la scala logaritmica. Nintendo documenta Chat C sul Pro Controller 2, sul Joy-Con 2 destro e sul GameCube per Switch 2. [SDL Switch 2](https://github.com/libsdl-org/SDL/blob/release-3.4.20/src/joystick/hidapi/SDL_hidapi_switch2.c) · [Acquisizioni e temporizzazione in nxnative](https://github.com/seedds/nxnative/pull/1349) · [Nintendo Support](https://en-americas-support.nintendo.com/app/answers/detail/a_id/68390/)

La suite verifica i byte e la sequenza logica degli effetti. Restano da misurare latenza BLE, resa percepita dei nuovi effetti e durata dei colpi sui motori reali. Le verifiche hardware nelle sezioni successive si riferiscono alla revisione precedente. Per Switch 1 e NSO GameCube la conversione da HD Rumble 2 riduce ciascun blocco alla banda di picco o allo stato del motore.

## USB/IP Switch 2 Pro

La build iniziale lasciava `USB\VID_057E&PID_2069\NX2WIN0001` con codice PnP 10 su `usbccgp`. Il descrittore di configurazione dichiarava 66 byte e ne forniva 64. Mancavano il descrittore OS per WinUSB, l’interfaccia HID Game Pad corretta e il report 0x05 usato dal client SDL per i controlli e il movimento.

La revisione espone due funzioni USB a velocità piena, usa descrittori Microsoft OS 1.0 per l’associazione WinUSB e usa una revisione e un seriale distinti dall’implementazione precedente. Il server gestisce annullamento dei trasferimenti, letture pendenti, inizializzazione, blocchi SPI e selezione dei report 0x05/0x09. La UI distingue l’avvio del servizio dall’inizializzazione da parte dell’host. [Protocollo USB/IP](https://cdn.kernel.org/doc/html/latest/usb/usbip_protocol.html) · [SDL 3.4.20](https://github.com/libsdl-org/SDL/blob/release-3.4.20/src/joystick/hidapi/SDL_hidapi_switch2.c) · [Descrittori e sequenza Switch 2](https://github.com/hifihedgehog/HIDMaestro/issues/66)

## Controller fisici e backend

- La scoperta BLE usa VID/PID del produttore e caratteristiche GATT precise. Le risposte di comando devono corrispondere al sottocomando e le letture SPI devono riportare l’indirizzo richiesto. Il parser condiviso valida dimensione, ID e formato prima di inviare lo stato.
- I controller Switch 2 cablati possono inizializzarsi attraverso la funzione WinUSB del proprio contenitore Windows. BLE e USB leggono la calibrazione SPI. Per Switch 1 HID è stata aggiunta la lettura della calibrazione utente e di fabbrica. [Driver di riferimento Switch 2](https://github.com/SmokeyMcGames/Switch2NativeControllerDriver/tree/1e5f843955d80189565dc36b73a53c99efe1f1d5) · [Ricerca SPI Switch 1](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering/blob/master/spi_flash_notes.md)
- La disconnessione manuale rimuove il dispositivo dalla scoperta, rilascia le risorse e azzera l’uscita se il dispositivo selezionato scompare. Una coppia rilascia entrambe le metà. La ricerca manuale riabilita i tentativi. La chiusura HID attende il completamento della lettura nativa prima di distruggere l’handle.
- Il bridge invia un report ViGEm per stato, allinea la disposizione posizionale dei pulsanti e tratta GL/GR come ingressi rimappabili. Il percorso VHF Switch Pro evita la pausa di 80 ms dopo ogni comando. DualSense e Xbox One HID accettano rimappature dei grilletti digitali. DSU distingue uno slot vuoto, espone i campi analogici corretti e inoltra la batteria quando il protocollo fisico fornisce un livello. [Protocollo DSU](https://github.com/v1993/cemuhook-protocol)
- Il DSP usa l’ordine delle bande Switch 2 osservato in SDL e nel driver di riferimento. Sono state corrette la codifica della frequenza e la lettura dell’ampiezza Switch 1. Il magnetometro può correggere lentamente lo yaw quando la lettura è plausibile. [Tabella HD Rumble Switch 1](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering/blob/master/rumble_data_table.md) · [Protocollo del driver di riferimento](https://github.com/SmokeyMcGames/Switch2NativeControllerDriver/blob/1e5f843955d80189565dc36b73a53c99efe1f1d5/src/protocol.cpp)
- Il comando Steam richiede una chiusura normale e avvia Steam con `-nojoy`. L’app riferisce l’avvio osservato e la diagnostica USB/IP resta indipendente da questo comando.

## Interfaccia e batteria

- Le schede WPF usano controlli tematici, focus visibile, etichette e stati di disponibilità. Il pannello di input mostra la telemetria del controller selezionato. I sei backend hanno nomi distinti. Le opzioni senza un comportamento proprio sono state rimosse.
- I profili hanno nomi persistenti, una bozza separata dalle impostazioni salvate e associazioni per controller. L’archivio è scritto in un file temporaneo prima della sostituzione. I controller HID usano un ID stabile per il percorso Windows.
- La batteria Nintendo usa il livello discreto e il flag di ricarica quando disponibili, altrimenti la tensione del report 0x05 e il suo campo di carica grezzo. La UI evita la precedente conversione lineare tensione → percentuale. In una coppia Joy-Con le letture sono individuali.
- Xbox può esporre le capacità attraverso Windows Gaming Input o i quattro livelli XInput. DualShock 4, DualSense e DualSense Edge espongono intervalli quando inviano report HID estesi. La scheda indica i dati indisponibili. [Windows Gaming Input](https://learn.microsoft.com/en-us/uwp/api/windows.gaming.input.rawgamecontroller.trygetbatteryreport) · [XInput](https://learn.microsoft.com/en-us/windows/win32/api/xinput/ns-xinput-xinput_battery_information) · [Driver Sony Linux](https://github.com/torvalds/linux/blob/master/drivers/hid/hid-playstation.c)

## Verifiche su Windows

- La suite locale controlla descrittori USB e WinUSB, report Switch 2, calibrazione sintetica, decoder batteria, codifica della vibrazione, fusione magnetica e scambio UDP DSU.
- Windows ha enumerato il dispositivo composito, il figlio HID e il figlio WinUSB con stato OK. Un test con la SDL 3.4.20 della build di gioco ha rilevato pulsante sud, entrambi gli stick, ZL, giroscopio e comandi di vibrazione.
- Nel menu di TOTK Recomp un comando D-pad inviato attraverso il backend USB/IP ha cambiato la selezione con Steam chiuso. Il movimento del menu è stato ripetuto dopo un nuovo attach con Steam avviato con `-nojoy -silent`.
- Il Switch 2 Pro fisico ha inviato report BLE da 63 byte. Il campione batteria era 3145 mV. La X ha rimosso il dispositivo ed è rimasto disconnesso durante la scansione automatica. È stato verificato il salvataggio di un profilo dalla UI e il ripristino al riavvio.

## Copertura residua

Restano le prove su hardware per Switch 1, entrambe le generazioni di Joy-Con, GameCube, Xbox e PlayStation. Restano anche le prove end-to-end dei profili VHF e della riconnessione BLE dopo lo spegnimento del controller. L’app usa una sola uscita attiva alla volta. La modalità Switch 2 via USB/IP richiede un client di gioco che supporti il protocollo Switch 2 e i driver Windows necessari. Per verificare rumble, giroscopio e altri comandi in gioco resta una sessione di gameplay con hardware attivo.

## Verifica sull’installazione di gioco aggiornata

L’installazione TotK Recomp 0.4.0 aperta durante il nuovo test caricava la SDL 3.4.20 standard, da 2848768 byte. Il suo hash corrispondeva alla copia originale e nel processo mancava libusb. Ho salvato una copia della DLL originale accanto al gioco, ripristinato la build SDL 3.4.20 compatibile già compilata e riavviato il gioco. Il processo ha caricato libusb. Il log di questa esecuzione ha registrato l’apertura del gamepad, campioni di movimento reali e i comandi DOWN e UP del Pro 2 fisico. [SDL e libusb per Switch 2 su Windows](https://github.com/libsdl-org/SDL/issues/14742)

NxToWin ora mostra l’ora dell’ultima richiesta del protocollo USB/IP e offre una verifica della DLL SDL selezionata. Un aggiornamento del gioco può sostituire quella DLL.
