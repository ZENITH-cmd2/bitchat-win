using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;

// Decides whether a Windows PC can be a real bitchat mesh node, or only a
// listener. Peripheral role is the hard requirement: without it the machine can
// hear the mesh but never announce itself, so no one can connect to it.

Console.WriteLine("=== BLE capability probe ===\n");

var radios = await Radio.GetRadiosAsync();
foreach (var radio in radios.Where(r => r.Kind == RadioKind.Bluetooth))
{
    Console.WriteLine($"radio: {radio.Name}  state={radio.State}");
}

var adapter = await BluetoothAdapter.GetDefaultAsync();
if (adapter is null)
{
    Console.WriteLine("\nNESSUN ADATTATORE BLUETOOTH ACCESSIBILE");
    return;
}

Console.WriteLine($"\naddress                : {adapter.BluetoothAddress:X12}");
Console.WriteLine($"LowEnergySupported     : {adapter.IsLowEnergySupported}");
Console.WriteLine($"CentralRoleSupported   : {adapter.IsCentralRoleSupported}");
Console.WriteLine($"PeripheralRoleSupported: {adapter.IsPeripheralRoleSupported}");
Console.WriteLine($"AdvertisementOffload   : {adapter.IsAdvertisementOffloadSupported}");

// Capability flags can lie; the only honest test is to actually publish a GATT
// service and start advertising, which is exactly what a mesh node must do.
Console.WriteLine("\n--- prova reale: pubblicazione servizio GATT + advertising ---");
try
{
    // bitchat's service UUID, so the probe exercises the real thing.
    var serviceUuid = Guid.Parse("F47B5E2D-4A9E-4C5A-9B3F-8E1D2C3A4B5C");
    var result = await GattServiceProvider.CreateAsync(serviceUuid);
    Console.WriteLine($"CreateAsync            : {result.Error}");

    if (result.Error == BluetoothError.Success)
    {
        var provider = result.ServiceProvider;

        // A service with no characteristics is a common reason advertising is
        // refused, so give it the read/write/notify shape a mesh node needs.
        var charUuid = Guid.Parse("A1B2C3D4-E5F6-4A5B-8C9D-0E1F2A3B4C5D");
        var charResult = await provider.Service.CreateCharacteristicAsync(charUuid,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read |
                                           GattCharacteristicProperties.Write |
                                           GattCharacteristicProperties.Notify,
                ReadProtectionLevel = GattProtectionLevel.Plain,
                WriteProtectionLevel = GattProtectionLevel.Plain
            });
        Console.WriteLine($"characteristic         : {charResult.Error}");

        var statuses = new List<string>();
        provider.AdvertisementStatusChanged += (s, _) => { lock (statuses) statuses.Add(s.AdvertisementStatus.ToString()); };

        var parameters = new GattServiceProviderAdvertisingParameters
        {
            IsDiscoverable = true,
            IsConnectable = true
        };
        provider.StartAdvertising(parameters);

        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(1000);
            Console.WriteLine($"  t+{i + 1}s status        : {provider.AdvertisementStatus}");
            if (provider.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started) break;
        }

        var final = provider.AdvertisementStatus;
        lock (statuses) Console.WriteLine($"transizioni            : {string.Join(" -> ", statuses)}");
        provider.StopAdvertising();

        Console.WriteLine(final == GattServiceProviderAdvertisementStatus.Started
            ? "ESITO: peripheral FUNZIONANTE — il PC puo' fare da nodo mesh"
            : $"ESITO: peripheral NON funzionante (stato finale: {final})");
    }
    else
    {
        Console.WriteLine("ESITO: peripheral NON disponibile");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"eccezione: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine("ESITO: peripheral NON disponibile");
}

// Distinguishes "this radio cannot advertise at all" from "it can advertise but
// not host a connectable GATT server". The mesh needs the latter, but knowing
// which wall we hit says whether the driver or the hardware is the problem.
Console.WriteLine("\n--- prova reale: advertising semplice (senza GATT) ---");
try
{
    var publisher = new BluetoothLEAdvertisementPublisher();
    publisher.Advertisement.ManufacturerData.Add(
        new BluetoothLEManufacturerData(0xFFFF, new Windows.Storage.Streams.DataWriter().DetachBuffer()));

    var states = new List<string>();
    publisher.StatusChanged += (s, _) => { lock (states) states.Add(s.Status.ToString()); };
    publisher.Start();

    for (int i = 0; i < 5; i++)
    {
        await Task.Delay(1000);
        if (publisher.Status == BluetoothLEAdvertisementPublisherStatus.Started) break;
    }

    Console.WriteLine($"publisher status       : {publisher.Status}");
    lock (states) Console.WriteLine($"transizioni            : {string.Join(" -> ", states)}");
    publisher.Stop();

    Console.WriteLine(publisher.Status == BluetoothLEAdvertisementPublisherStatus.Started
        ? "ESITO: la radio SA annunciare (il limite e' il server GATT)"
        : "ESITO: la radio NON annuncia affatto");
}
catch (Exception ex)
{
    Console.WriteLine($"eccezione: {ex.GetType().Name}: {ex.Message}");
}

// Scanning is the other half, and it is far more reliably supported.
Console.WriteLine("\n--- prova reale: scansione BLE 8s ---");
var seen = new HashSet<ulong>();
var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
watcher.Received += (_, e) => { lock (seen) seen.Add(e.BluetoothAddress); };
watcher.Start();
await Task.Delay(8000);
watcher.Stop();
Console.WriteLine($"dispositivi BLE distinti visti: {seen.Count}");
Console.WriteLine(seen.Count > 0 ? "ESITO: scansione (central) FUNZIONANTE" : "ESITO: nessun dispositivo visto (scansione forse bloccata)");
