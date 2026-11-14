# Driver e compatibilità

NxToWin riceve i report di un controller Nintendo e li traduce per l’uscita scelta. I pulsanti fisici rimappabili e le funzioni esposte al gioco si configurano nello stesso pannello.

| Uscita | Driver | Impostazioni dedicate e dati esposti |
| --- | --- | --- |
| Xbox 360 · XInput | [ViGEmBus](https://github.com/nefarius/ViGEmBus) | Pulsanti XInput, motori grande e piccolo. Home può diventare Xbox Guide. |
| Xbox One · VHF | [WinUHid](https://github.com/lurebat/WinUHid) | Profilo HID Xbox, vibrazione dei grilletti configurabile separatamente. |
| DualShock 4 · ViGEm | [ViGEmBus](https://github.com/nefarius/ViGEmBus) | Cattura può diventare Share o clic del touchpad. |
| DualSense · VHF | [WinUHid](https://github.com/lurebat/WinUHid) | Clic da Cattura, contatto da stick destro mentre si tiene premuto −, movimento e conversione del feedback dei grilletti in vibrazione. |
| Switch Pro · VHF | [WinUHid](https://github.com/lurebat/WinUHid) | Report Switch Pro con movimento, HD Rumble e protezione dello streaming da Steam. |
| Switch 2 Pro · USB/IP | [usbip-win2](https://github.com/vadimgrn/usbip-win2) | Profilo USB composito 057E:2069 con movimento, HD Rumble 2 e inoltro Chat C, GL e GR quando presenti sul controller fisico. |

Chat C compare solo con un pulsante fisico compatibile e con l’uscita Switch 2 Pro. GL e GR nella colonna del controller fisico possono essere assegnati a pulsanti comuni anche quando il gioco usa XInput o un profilo PlayStation. SL e SR appartengono ai Joy-Con. Il mouse ottico è una funzione del Joy-Con 2 verso Windows. [Profili Nintendo e byte dei pulsanti](https://github.com/ndeadly/switch2_controller_research/blob/master/hid_reports.md) · [Report nativo DualSense WinUHid](https://github.com/lurebat/WinUHid/blob/main/WinUHidDevs/WinUHidPS5.h)

## Audio DualSense

L’uscita DualSense VHF di NxToWin espone HID. Altoparlante, cuffie, microfono e aptica audio dei giochi richiedono un dispositivo USB composito con interfacce USB Audio Class. Su un DualSense USB reale i canali 1 e 2 del flusso di uscita sono audio, i canali 3 e 4 sono aptica. Un controller Nintendo usato come sorgente offre i propri attuatori e i propri ingressi. Un’implementazione composita deve decidere come indirizzare i canali audio e convertire l’aptica verso quel dispositivo. [Profili USB compositi di HIDMaestro](https://github.com/hifihedgehog/HIDMaestro#controller-audio-and-haptics-composite-usb-personas)

| Implementazione esaminata | Funzioni documentate |
| --- | --- |
| [HIDMaestro](https://github.com/hifihedgehog/HIDMaestro) | Profili Nintendo, Xbox e PlayStation. Il profilo `dualsense-composite` espone audio, microfono, HID e dati aptici tramite SDK. |
| [Switch2Connect](https://github.com/TommyWabg/Switch2Connect#readme) | DualSense via USB/IP e conversione dell’aptica audio per i controller Switch 2. |
| [CTM-USBIP](https://github.com/CTM-Bridge/CTM-USBIP) | Trasporto dei report e dell’audio verso controller fisici Sony tramite un agente USB/IP. |
| [LuminalVGBus](https://github.com/NortheBridge/LuminalVGBus) | Alternativa mantenuta per Xbox 360 e DualShock 4 con una propria interfaccia driver. Richiede un client compatibile. |

Le integrazioni di NxToWin restano quelle della prima tabella. ViGEmBus è archiviato upstream.

## Letture batteria

Le percentuali provengono dalla proprietà Windows del dispositivo, dal Battery Service Bluetooth quando esposto, oppure dalle capacità Windows Gaming Input. I report Nintendo forniscono anche livelli discreti, flag di alimentazione e millivolt. Switch 1 interroga il sottocomando HID 0x50 durante lo streaming. Switch 2 usa la tensione di 0x05 o i livelli e i flag dei report compatti. I report Sony estesi restituiscono stati e fasce di carica. XInput fornisce quattro livelli. USB, Bluetooth, alimentazione esterna e carica sono indicati secondo i dati della sorgente. [Proprietà Windows BatteryLife](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-devices-batterylife) · [Battery Service su Windows](https://learn.microsoft.com/en-us/windows-hardware/design/accessory-guidelines/bluetooth-accessory-guidelines/bluetooth-accessory-guidelines-battery-profiles-services) · [Switch 1 HID](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering/blob/master/bluetooth_hid_subcommands_notes.md#subcommand-0x50-get-regulated-voltage) · [Switch 2, campi batteria e carica](https://github.com/novakpetya/linux-switch2/blob/main/PROTOCOL.md#35-common-battery-fields)
