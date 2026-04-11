using System;

using Loupedeck.MacroClaudePlugin.Status;

namespace Loupedeck.MacroClaudePlugin;

// Plugin entry point. Owns the StatusReader lifecycle and routes
// session updates into slot assignments published on SlotBus.
public class MacroClaudePlugin : Plugin
{
    public override Boolean UsesApplicationApiOnly => true;
    public override Boolean HasNoApplication => true;

    private StatusReader? _statusReader;
    private SlotAssigner? _slotAssigner;
    private Guid _busToken = Guid.Empty;

    public MacroClaudePlugin()
    {
        PluginLog.Init(this.Log);
        PluginResources.Init(this.Assembly);
    }

    public override void Load()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Nine = one MX Creative Console profile page, matching the
        // nine ClaudeSlot1Command..ClaudeSlot9Command classes in
        // src/Actions/. If you add more slot commands, bump this.
        this._slotAssigner = new SlotAssigner(maxSlots: SlotBus.ValidSlotCount);

        // Claim exclusive ownership of SlotBus. Any previous Plugin
        // instance that LPS failed to fully unload still has its old
        // token and will be silently rejected from publishing into
        // the bus. The snapshot store is wiped so stale zombie entries
        // never reach subscribers after a reload.
        this._busToken = SlotBus.AcquireOwnership();

        this._statusReader = new StatusReader(home);
        this._statusReader.SessionUpdated += this.OnSessionUpdated;
        this._statusReader.SessionRemoved += this.OnSessionRemoved;

        try
        {
            this._statusReader.Start();
            PluginLog.Info("macro-claude: StatusReader started");
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "macro-claude: StatusReader failed to start");
        }
    }

    public override void Unload()
    {
        if (this._statusReader != null)
        {
            this._statusReader.SessionUpdated -= this.OnSessionUpdated;
            this._statusReader.SessionRemoved -= this.OnSessionRemoved;
            this._statusReader.Dispose();
            this._statusReader = null;
        }
        this._slotAssigner = null;
        this._busToken = Guid.Empty;
    }

    private void OnSessionUpdated(Object? sender, SessionSnapshot snapshot)
    {
        var assigner = this._slotAssigner;
        if (assigner == null)
        {
            return;
        }
        var slot = assigner.Ensure(snapshot.SessionId);
        if (slot < 0)
        {
            // All slots occupied — ignore. A future version will scroll
            // to additional profile pages.
            PluginLog.Warning($"macro-claude: no free slot for {snapshot.SessionId}");
            return;
        }
        PluginLog.Verbose(
            $"macro-claude: session {snapshot.SessionId} → slot {slot} state={snapshot.State} name={snapshot.ShortName}");
        SlotBus.Publish(this._busToken, slot, snapshot);
    }

    private void OnSessionRemoved(Object? sender, String sessionId)
    {
        var assigner = this._slotAssigner;
        if (assigner == null)
        {
            return;
        }
        var slot = assigner.Release(sessionId);
        if (slot < 0)
        {
            return;
        }
        PluginLog.Verbose($"macro-claude: session {sessionId} removed from slot {slot}");
        SlotBus.Publish(this._busToken, slot, null);
    }
}
