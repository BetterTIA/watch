using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Blocks.Interface;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.WatchAndForceTables;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BtiaWatch
{
    /// <summary>
    /// IWatchPortal over TIA Portal Openness V21. Attaches to a running TIA Portal and never starts one.
    /// </summary>
    public sealed class OpennessPortal : IWatchPortal
    {
        private readonly Dictionary<int, TiaPortal> _attached = new();
        private readonly object _lock = new();

        public string GetOnlineState(int? portalId, string? plc)
        {
            lock (_lock)
            {
                return Online(FindPlc(portalId, plc)).State.ToString();
            }
        }

        public string GoOnline(int? portalId, string? plc, string pcInterface, string address)
        {
            lock (_lock)
            {
                var provider = Online(FindPlc(portalId, plc));
                var interfaces = provider.Configuration.Modes
                    .SelectMany(m => m.PcInterfaces.Select(i => (Mode: m.Name, Interface: i)))
                    .ToList();
                var matches = interfaces.Where(i => i.Interface.Name == pcInterface).ToList();
                if (matches.Count != 1)
                {
                    throw new ArgumentException($"{matches.Count} PC interfaces are named '{pcInterface}'. Available: " +
                        string.Join(", ", interfaces.Select(i => $"{i.Mode}: {i.Interface.Name} #{i.Interface.Number}")));
                }

                // shortcut: address created on the PC interface as GoOnline(ConfigurationAddress) expects; not yet tried against a PLC.
                var addresses = matches[0].Interface.Addresses;
                provider.GoOnline(addresses.Find(address) ?? addresses.Create(address));
                return provider.State.ToString();
            }
        }

        public string GoOffline(int? portalId, string? plc)
        {
            lock (_lock)
            {
                var provider = Online(FindPlc(portalId, plc));
                provider.GoOffline();
                return provider.State.ToString();
            }
        }

        public string ExportDbSnapshot(int? portalId, string? plc, string dbName)
        {
            lock (_lock)
            {
                var software = FindPlc(portalId, plc).Software;
                // Technology objects (PID_Temp, PID_Compact, axes...) are instance DBs, but they live in the
                // technology objects folder, not among the program blocks (V21: TechnologicalInstanceDB : InstanceDB).
                var db = AllBlocks(software.BlockGroup).FirstOrDefault(b => b.Name == dbName) as DataBlock
                    ?? AllTechnologicalObjects(software.TechnologicalObjectGroup).FirstOrDefault(t => t.Name == dbName)
                    ?? throw new ArgumentException($"Data block '{dbName}' not found among the program blocks or the technology objects.");

                // F_DB and every other fail-safe language: the tool keeps away from the safety program.
                if (db.ProgrammingLanguage.ToString().StartsWith("F_", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"'{dbName}' is a fail-safe data block; it is not read.");
                }

                var values = db.Interface.GetService<ValueService>()
                    ?? throw new InvalidOperationException($"'{dbName}' offers no value service.");
                // The snapshot service sits on the block itself; on the interface it is null (spike, V21).
                var snapshot = db.GetService<InterfaceSnapshot>()
                    ?? throw new InvalidOperationException($"'{dbName}' offers no snapshot service.");
                values.CreateSnapshot();

                var file = new FileInfo(Path.Combine(Path.GetTempPath(), $"btia-watch-{Guid.NewGuid():N}.xml"));
                try
                {
                    // The values sit in the read-only attribute 'Snapshot'; ExportOptions.None leaves it out (live test, V21).
                    snapshot.Export(file, ExportOptions.WithReadOnly);
                    return File.ReadAllText(file.FullName);
                }
                finally
                {
                    if (File.Exists(file.FullName))
                    {
                        File.Delete(file.FullName);
                    }
                }
            }
        }

        public IntPtr ShowInEditor(int? portalId, string? plc, WatchTarget target, string name)
        {
            lock (_lock)
            {
                var found = FindPlc(portalId, plc);
                var software = found.Software;

                switch (target)
                {
                    case WatchTarget.WatchTable:
                        (AllWatchTables(software.WatchAndForceTableGroup).FirstOrDefault(t => t.Name == name)
                            ?? throw new ArgumentException($"Watch table '{name}' not found.")).ShowInEditor();
                        break;
                    case WatchTarget.TagTable:
                        (AllTagTables(software.TagTableGroup).FirstOrDefault(t => t.Name == name)
                            ?? throw new ArgumentException($"Tag table '{name}' not found.")).ShowInEditor();
                        break;
                    case WatchTarget.Block:
                        (AllBlocks(software.BlockGroup).FirstOrDefault(b => b.Name == name)
                            ?? throw new ArgumentException($"Block '{name}' not found.")).ShowInEditor();
                        break;
                }

                var hwnd = Process.GetProcessById(found.PortalId).MainWindowHandle;
                if (hwnd == IntPtr.Zero)
                {
                    throw new InvalidOperationException("TIA Portal has no main window (started without user interface?).");
                }
                return hwnd;
            }
        }

        private sealed class FoundPlc
        {
            public int PortalId;
            public DeviceItem Cpu = null!;
            public PlcSoftware Software = null!;
        }

        private static OnlineProvider Online(FoundPlc found) =>
            found.Cpu.GetService<OnlineProvider>() ?? throw new InvalidOperationException("This PLC has no online provider.");

        private FoundPlc FindPlc(int? portalId, string? plc)
        {
            var (id, portal) = Attach(portalId);
            // shortcut: local projects only; a multiuser local session is not looked at.
            var project = portal.Projects.FirstOrDefault()
                ?? throw new InvalidOperationException("TIA Portal has no project open.");

            var plcs = new List<FoundPlc>();
            foreach (var device in AllDevices(project))
            {
                foreach (var item in AllItems(device.DeviceItems))
                {
                    if (item.GetService<SoftwareContainer>()?.Software is PlcSoftware software)
                    {
                        plcs.Add(new FoundPlc { PortalId = id, Cpu = item, Software = software });
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(plc))
            {
                return plcs.Count == 1
                    ? plcs[0]
                    : throw new ArgumentException($"The project has {plcs.Count} PLCs; pass 'plc'. Found: {string.Join(", ", plcs.Select(p => p.Software.Name))}");
            }

            return plcs.FirstOrDefault(p => p.Software.Name == plc)
                ?? throw new ArgumentException($"PLC '{plc}' not found. Found: {string.Join(", ", plcs.Select(p => p.Software.Name))}");
        }

        private (int Id, TiaPortal Portal) Attach(int? portalId)
        {
            var processes = TiaPortal.GetProcesses();
            TiaPortalProcess process;
            if (portalId.HasValue)
            {
                process = processes.FirstOrDefault(p => p.Id == portalId.Value)
                    ?? throw new ArgumentException($"No TIA Portal with process id {portalId}.");
            }
            else if (processes.Count == 1)
            {
                process = processes[0];
            }
            else
            {
                throw new InvalidOperationException(processes.Count == 0
                    ? "No TIA Portal is running."
                    : $"{processes.Count} TIA Portal instances are running; pass 'portalId': {string.Join(", ", processes.Select(p => p.Id))}");
            }

            if (!_attached.TryGetValue(process.Id, out var portal))
            {
                portal = process.Attach();
                _attached[process.Id] = portal;
            }
            return (process.Id, portal);
        }

        private static IEnumerable<Device> AllDevices(Project project)
        {
            foreach (var device in project.Devices)
            {
                yield return device;
            }
            foreach (var device in project.DeviceGroups.SelectMany(AllDevices))
            {
                yield return device;
            }
            foreach (var device in project.UngroupedDevicesGroup.Devices)
            {
                yield return device;
            }
        }

        private static IEnumerable<Device> AllDevices(DeviceUserGroup group) =>
            group.Devices.Concat(group.Groups.SelectMany(AllDevices));

        private static IEnumerable<DeviceItem> AllItems(DeviceItemComposition items) =>
            items.SelectMany(i => new[] { i }.Concat(AllItems(i.DeviceItems)));

        private static IEnumerable<PlcBlock> AllBlocks(PlcBlockGroup group) =>
            group.Blocks.Concat(group.Groups.SelectMany(AllBlocks));

        private static IEnumerable<PlcBlock> AllBlocks(PlcBlockUserGroup group) =>
            group.Blocks.Concat(group.Groups.SelectMany(AllBlocks));

        private static IEnumerable<TechnologicalInstanceDB> AllTechnologicalObjects(TechnologicalInstanceDBGroup group) =>
            group.TechnologicalObjects.Concat(group.Groups.SelectMany(AllTechnologicalObjects));

        private static IEnumerable<PlcTagTable> AllTagTables(PlcTagTableSystemGroup group) =>
            group.TagTables.Concat(group.Groups.SelectMany(AllTagTables));

        private static IEnumerable<PlcTagTable> AllTagTables(PlcTagTableUserGroup group) =>
            group.TagTables.Concat(group.Groups.SelectMany(AllTagTables));

        private static IEnumerable<PlcWatchTable> AllWatchTables(PlcWatchAndForceTableSystemGroup group) =>
            group.WatchTables.Concat(group.Groups.SelectMany(AllWatchTables));

        private static IEnumerable<PlcWatchTable> AllWatchTables(PlcWatchAndForceTableUserGroup group) =>
            group.WatchTables.Concat(group.Groups.SelectMany(AllWatchTables));
    }
}
