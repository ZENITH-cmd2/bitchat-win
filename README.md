# bitchat-win

Client Windows **non ufficiale** per i canali geohash di [bitchat](https://github.com/permissionlesstech/bitchat).

Implementa la metà "internet" del protocollo — le **location channels** trasportate su
relay Nostr — e interopera con i client bitchat ufficiali per iOS e Android.
La mesh Bluetooth non è implementata; vedi *Senza internet* per che cosa
l'hardware Windows riesce e non riesce a fare.

## Avvio rapido

```
publish\BitchatWin.exe
```

Serve il runtime **.NET 9** (già presente se hai l'SDK). Nessuna installazione,
nessun account, nessuna registrazione.

Opzioni da riga di comando:

| Comando | Effetto |
|---|---|
| `BitchatWin.exe` | apre la finestra |
| `BitchatWin.exe --join u0nd9` | apre ed entra subito nel canale |
| `BitchatWin.exe --selftest` | verifica crittografia, geohash e selezione relay |
| `BitchatWin.exe --selftest --scan` | osserva 45s di traffico bitchat reale (sola lettura) |
| `BitchatWin.exe --selftest --listen u0nd9` | ascolta un canale specifico (sola lettura) |
| `BitchatWin.exe --selftest --sendtest` | pubblica un evento di prova in un geohash deserto |
| `BitchatWin.exe --hidden` | parte già nascosta nella tray |

## Come funziona

**Identità.** Al primo avvio genera un seme di 32 byte in
`%APPDATA%\bitchat-win\device-seed.bin`, protetto con DPAPI. Da quel seme deriva
una chiave secp256k1 **diversa per ogni canale**:

```
HMAC-SHA256(seme, geohash ‖ uint32BE(i)) → chiave privata del canale
```

Così la stessa installazione non è collegabile fra un canale e l'altro. È la
stessa derivazione di `NostrIdentityBridge.deriveIdentity(forGeohash:)` nel
client Swift, quindi il comportamento è identico a quello ufficiale.

**Canali.** Un canale è un geohash: `u0` una macroregione, `u0nd9` una città,
`u0nd9mk` un isolato. Se non conosci il tuo, inserisci lat/lon e premi
*calcola geohash*.

**Relay.** Il client decodifica il centro del geohash e apre i **5 relay più
vicini** fra i **326 distinti** ricavati da `Assets/online_relays_gps.csv`
(441 righe, copiate dal repo bitchat e aggiornate all'avvio da upstream).
Ordinamento per distanza haversine, parità risolta sul nome host: stesso
geohash → stessi relay per tutti, che è la condizione perché le persone si
incontrino.

La normalizzazione degli host replica `validatedDirectoryAddress` del client
Swift, incluso il dettaglio decisivo: la porta `:443` esplicita viene rimossa,
così `no.str.cr` e `no.str.cr:443` collassano in un solo relay. Trattarli come
due voci distinte lascerebbe questo client con meno relay effettivi degli altri
e con un quinto relay diverso dal loro. Un file CSV con una riga malformata
viene **rifiutato per intero**, non ripulito riga per riga, sempre per non
divergere in silenzio dal resto della rete.

**Messaggi.** Evento Nostr `kind 20000`, tag `["g", geohash]` e `["n", nick]`,
firma Schnorr BIP-340. Prima della firma viene minato un proof-of-work NIP-13 da
8 bit (`["nonce", <16 hex>, "8"]`) per non incorrere nei rate limit dei relay.
Il nome mostrato è `nick#xxxx`, con le ultime 4 cifre della chiave pubblica del
canale — la convenzione usata da bitchat.

**Presenza.** Battito `kind 20001` ogni 40–80 secondi, che popola la lista dei
presenti. **Non viene inviato a precisione 6 o 7**: annunciarsi su un'area di
poche centinaia di metri equivale a dichiarare dove si è fisicamente. Stessa
scelta del client ufficiale.

**Ricezione.** Ogni evento viene ricalcolato e verificato (event ID + firma)
prima di raggiungere l'interfaccia, così un relay ostile non può fabbricare
messaggi a nome altrui. I duplicati fra i 5 relay vengono uniti.

## Discrezione sullo schermo

La finestra è pensata per non dichiarare cosa sia a chi guarda il monitor di
sfuggita.

| Comando | Effetto |
|---|---|
| `Esc` | nasconde la finestra all'istante; il canale resta connesso e i messaggi continuano ad arrivare |
| `Ctrl+M` | modalità compatta: pannello 360×300 con la sola conversazione e il campo di scrittura |
| `Ctrl+T` | sempre in primo piano |
| clic sull'icona nella tray | mostra o nasconde |

Le scorciatoie usano il *tunnelling* degli eventi, quindi funzionano anche
mentre stai scrivendo in un campo di testo: un tasto di fuga che si attiva solo
quando nessun controllo ha il fuoco non serve a niente.

Nella riga **aspetto** ci sono inoltre:

- **fuori da barra e Alt+Tab** — toglie la finestra dalla barra delle
  applicazioni e, tramite `WS_EX_TOOLWINDOW`, anche dall'elenco Alt+Tab, che è
  il posto dove qualcuno la noterebbe più facilmente;
- **opacità** regolabile da 35% a 100%;
- **titolo** libero: quello che appare nella barra del titolo e in Alt+Tab. Il
  valore predefinito è `Note`, non il nome dell'applicazione.

La chiusura della finestra la nasconde invece di uscire, così un Alt+F4 distratto
non fa perdere il canale; per uscire davvero si usa la voce *Esci* nel menu della
tray. Tutte le preferenze sono salvate in
`%APPDATA%\bitchat-win\settings.json`, e `--hidden` avvia l'app già nascosta.

## Senza internet

La mesh Bluetooth **non è implementata**, e su questo hardware non lo sarebbe
comunque per intero. La sonda in `tools/ble-probe` (`dotnet run`) misura le tre capacità
che servono:

| Capacità | Adattatore Realtek provato |
|---|---|
| Scansione BLE (central) | funziona — 40 dispositivi visti in 8 s |
| Advertising semplice | funziona — `Waiting → Started` |
| Server GATT connettibile (peripheral) | **`Aborted`**, anche con caratteristica valida |

Nella mesh bitchat i peer si collegano al server GATT l'uno dell'altro: senza
quello un PC può vedere la mesh e collegarsi ai telefoni, ma non farsi trovare,
e due PC non si parlerebbero mai. `IsPeripheralRoleSupported` riporta `True`, ma
l'annuncio viene comunque interrotto — la capacità dichiarata dal driver non
corrisponde a quella reale.

Resta praticabile una **rete locale senza internet**: i client bitchat accettano
relay personalizzati, quindi un relay Nostr in ascolto sulla LAN fa funzionare i
canali geohash fra dispositivi collegati allo stesso Wi-Fi anche senza uscita
verso internet. Non è la mesh, ma non richiede né internet né hardware che qui
non funziona.

## Verifica di interoperabilità

`--selftest` copre vettori geohash noti, serializzazione canonica NIP-01,
firme Schnorr, rifiuto di eventi manomessi, proof-of-work e determinismo della
selezione relay.

`--scan` è la prova di **ricezione**: si iscrive in sola lettura al traffico
bitchat globale e conta gli eventi che superano la verifica. Nell'esecuzione di
collaudo sono arrivati **2914 eventi verificati su 222 canali attivi**, tutti
firmati da client bitchat reali — il che dimostra che la serializzazione
canonica di questo client è byte-esatta rispetto alla rete: se non lo fosse,
nessuno di quegli event ID avrebbe combaciato.

`--sendtest` è la prova di **invio**: pubblica un singolo evento in un geohash
sopra Point Nemo (`#1r23bn78`, oceano deserto) con un'identità usa-e-getta, e
riporta la risposta di ogni relay. Nel collaudo tre relay hanno risposto
`ACCEPTED` e l'evento è tornato indietro sulla sottoscrizione, quindi è stato
davvero ritrasmesso agli iscritti.

Questa prova ha trovato due difetti reali: un crash a ogni invio
(`RememberSubscriptionState` leggeva come stringa il campo che in un frame
`EVENT` è un oggetto) e la mancata normalizzazione della porta `:443` descritta
sopra. Nessuno dei due era visibile senza pubblicare davvero.

## Struttura

```
Protocol/Geohash.cs             encoder/decoder base32
Protocol/GeoRelayDirectory.cs   441 righe CSV -> 326 relay, selezione dei 5 più vicini
Nostr/NostrEvent.cs             serializzazione canonica, event ID, firma/verifica
Nostr/NostrCrypto.cs            secp256k1 Schnorr BIP-340
Nostr/NostrIdentity.cs          derivazione identità per canale
Nostr/NostrPoW.cs               proof-of-work NIP-13
Nostr/RelayPool.cs              WebSocket multi-relay, riconnessione, dedup
Services/GeohashChannelService.cs  logica del canale: join, invio, presenza
Services/IdentityStore.cs       seme dispositivo protetto DPAPI
MainWindow.axaml                interfaccia Avalonia
SelfTest.cs                     verifiche headless
tools/ble-probe/                sonda capacità Bluetooth LE del PC
```

## Compilare

```
dotnet build
dotnet run -- --selftest
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Avalonia è fissato a **11.3.0**: la 12.x richiede Roslyn 4.14, cioè l'SDK .NET 10.

## Non implementato

Messaggi privati (envelope XChaCha20-Poly1305 `kind 1059`), mesh Bluetooth,
canali teleport, note persistenti `kind 1`, courier drops.

## Licenza

bitchat è di pubblico dominio; questo client segue la stessa scelta.
