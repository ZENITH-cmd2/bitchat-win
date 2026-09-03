using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;

// Decides whether a Windows PC can be a real bitchat mesh node, or only a
// listener. Peripheral role is the hard requirement: without it the machine can
// hear the mesh but never announce itself, so no one can connect to it.

Console.WriteLine("=== BLE capability probe ===\n");

foreach (var radio in (await Radio.GetRadiosAsync()).Where(r => r.Kind == RadioKind.Bluetooth))
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

// Capability flags are declarations, not behaviour. The only honest test is to
// publish a real GATT service and try to advertise it, which is exactly what a
// mesh node must do. Every variant below carries a characteristic: a service
// with none never advertises at all, which would make the comparison useless.
Console.WriteLine("\n--- server GATT: tre combinazioni a confronto ---");
await TryAdvertiseAsync("connettibile + individuabile", connectable: true, discoverable: true);
await TryAdvertiseAsync("connettibile, non individuabile", connectable: true, discoverable: false);
await TryAdvertiseAsync("individuabile, NON connettibile", connectable: false, discoverable: true);

// In WinRT connectable advertising is reachable ONLY through GattServiceProvider;
// this publisher is always non-connectable. So "publisher starts, provider does
// not" already isolates the peripheral role as the missing capability.
Console.WriteLine("\n--- advertising semplice, senza GATT (sempre non connettibile) ---");
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

    Console.WriteLine($"  status      : {publisher.Status}");
    lock (states) Console.WriteLine($"  transizioni : {string.Join(" -> ", states)}");
    publisher.Stop();
}
catch (Exception ex)
{
    Console.WriteLine($"  eccezione: {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine("\n--- scansione BLE 8s (ruolo central) ---");
var seen = new HashSet<ulong>();
var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
watcher.Received += (_, e) => { lock (seen) seen.Add(e.BluetoothAddress); };
watcher.Start();
await Task.Delay(8000);
watcher.Stop();
Console.WriteLine($"  dispositivi BLE distinti: {seen.Count}");
Console.WriteLine(seen.Count > 0 ? "  central FUNZIONANTE" : "  nessun dispositivo visto");

static async Task TryAdvertiseAsync(string label, bool connectable, bool discoverable)
{
    Console.WriteLine($"\n  [{label}]");
    try
    {
        var result = await GattServiceProvider.CreateAsync(Guid.Parse("F47B5E2D-4A9E-4C5A-9B3F-8E1D2C3A4B5C"));
        if (result.Error != BluetoothError.Success)
        {
            Console.WriteLine($"    CreateAsync : {result.Error}");
            return;
        }

        var provider = result.ServiceProvider;

        var charResult = await provider.Service.CreateCharacteristicAsync(
            Guid.Parse("A1B2C3D4-E5F6-4A5B-8C9D-0E1F2A3B4C5D"),
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read |
                                           GattCharacteristicProperties.Write |
                                           GattCharacteristicProperties.Notify,
                ReadProtectionLevel = GattProtectionLevel.Plain,
                WriteProtectionLevel = GattProtectionLevel.Plain
            });

        if (charResult.Error != BluetoothError.Success)
        {
            Console.WriteLine($"    caratteristica: {charResult.Error}");
            return;
        }

        var states = new List<string>();
        provider.AdvertisementStatusChanged += (s, _) => { lock (states) states.Add(s.AdvertisementStatus.ToString()); };

        provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsDiscoverable = discoverable,
            IsConnectable = connectable
        });

        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(1000);
            if (provider.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started) break;
        }

        Console.WriteLine($"    status      : {provider.AdvertisementStatus}");
        lock (states)
        {
            Console.WriteLine($"    transizioni : {(states.Count == 0 ? "(nessuna)" : string.Join(" -> ", states))}");
        }
        provider.StopAdvertising();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"    eccezione: {ex.GetType().Name}: {ex.Message}");
    }
}
