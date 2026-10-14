using System;

namespace BtiaWatch
{
    /// <summary>What <c>watch_screen</c> opens in the TIA Portal editor.</summary>
    public enum WatchTarget
    {
        WatchTable,
        TagTable,
        Block
    }

    /// <summary>
    /// The only door to TIA Portal. Every member reads; none of them changes values in the PLC.
    /// The tools talk to this interface so they can be tested without TIA Portal.
    /// </summary>
    public interface IWatchPortal
    {
        /// <summary>OnlineProvider.State of the CPU, e.g. "Online", "Offline".</summary>
        string GetOnlineState(int? portalId, string? plc);

        /// <summary>GoOnline() or GoOffline() on the CPU; returns the state afterwards.</summary>
        string SetOnline(int? portalId, string? plc, bool online);

        /// <summary>Takes a snapshot of the actual values of one data block and returns its SimaticML export.</summary>
        string ExportDbSnapshot(int? portalId, string? plc, string dbName);

        /// <summary>Opens the object in its editor and returns the handle of the TIA Portal main window.</summary>
        IntPtr ShowInEditor(int? portalId, string? plc, WatchTarget target, string name);
    }
}
